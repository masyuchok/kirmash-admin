using backend.Data;
using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public sealed class InvoiceExpenseExtractionService
{
    private readonly InvoicePdfTextExtractor _pdf;
    private readonly InvoiceLineItemParser _lineParser;
    private readonly GroqInvoiceExtractionService _groq;
    private readonly SupplyService _supplies;
    private readonly AppDbContext _db;

    public InvoiceExpenseExtractionService(
        InvoicePdfTextExtractor pdf,
        InvoiceLineItemParser lineParser,
        GroqInvoiceExtractionService groq,
        SupplyService supplies,
        AppDbContext db )
    {
        _pdf = pdf;
        _lineParser = lineParser;
        _groq = groq;
        _supplies = supplies;
        _db = db;
    }

    public async Task<VatReportExpenseInvoiceExtractResult> ExtractAsync(
        byte[] fileBytes,
        string fileName,
        string contentType,
        int? preferredSupplierId = null,
        CancellationToken cancellationToken = default )
    {
        if (!IsPdf( fileName, contentType, fileBytes ))
        {
            throw new InvalidOperationException(
                "Пакуль падтрымліваюцца толькі PDF-фактуры (тэкставыя)." );
        }

        string text = _pdf.ExtractText( fileBytes );
        List<string> typeNames = await _db.ExpenseInvoiceTypes
            .AsNoTracking()
            .OrderBy( t => t.Name )
            .Select( t => t.Name )
            .ToListAsync( cancellationToken );

        // 1) Groq: header/totals only (small prompt — fits free-tier TPM).
        VatReportExpenseInvoiceExtractResult result =
            await _groq.ExtractFromTextAsync( text, typeNames, cancellationToken );

        // 2) Deterministic line items from full PDF text (supports 100+ rows).
        IReadOnlyList<VatReportExpenseInvoiceExtractProduct> parsedLines = _lineParser.Parse( text );
        if (parsedLines.Count > 0)
        {
            result.Products = parsedLines.ToList();
            AppendWarning( result, $"З PDF прачытана радкоў тавараў: {parsedLines.Count}." );
        }
        else
        {
            // 3) Fallback: Groq over text chunks (slower, but works when layout/SKU parsing fails).
            await _groq.ExtractProductsFromTextChunksAsync( result, text, cancellationToken );
            if (result.Products.Count > 0)
            {
                AppendWarning(
                    result,
                    $"Радкі з PDF праз AI (пачкамі): {result.Products.Count}." );
            }
            else
            {
                AppendWarning(
                    result,
                    "Радкі тавараў з PDF не знойдзены — праверце, што гэта тэкставая (не скан) фактура." );
            }
        }

        List<Supplier> suppliers = await _db.Suppliers
            .AsNoTracking()
            .OrderBy( s => s.Name )
            .ToListAsync( cancellationToken );

        Supplier? matchedSupplier = null;
        if (preferredSupplierId is > 0)
        {
            matchedSupplier = suppliers.FirstOrDefault( s => s.Id == preferredSupplierId.Value );
        }

        if (matchedSupplier is null && !string.IsNullOrWhiteSpace( result.VendorName ))
        {
            matchedSupplier = MatchSupplier( result.VendorName, suppliers );
        }

        if (matchedSupplier is not null)
        {
            result.SuggestedSupplierId = matchedSupplier.Id;
            result.SuggestedSupplierName = matchedSupplier.Name;
        }

        if (result.Products.Count == 0)
        {
            return result;
        }

        // Match products only against the selected/recognized supplier catalog.
        if (matchedSupplier is null)
        {
            AppendWarning(
                result,
                "Пастаўшчык не выбраны — тавары не супастаўляліся. Выберыце пастаўшчыка і націсніце «Распазнаць» яшчэ раз." );
            return result;
        }

        List<SupplyCatalogProductItem> supplyCatalog =
            await _supplies.GetCatalogProductsAsync( matchedSupplier.Id );

        if (supplyCatalog.Count == 0)
        {
            AppendWarning(
                result,
                $"У пастаўшчыка «{matchedSupplier.Name}» няма тавараў у пастаўках — супаставіць няма з чым." );
            return result;
        }

        List<InvoiceCatalogMatchCandidate> candidates = supplyCatalog
            .Where( p => !string.IsNullOrWhiteSpace( p.ShopifyProductId ) )
            .Select( p => new InvoiceCatalogMatchCandidate
            {
                ShopifyProductId = p.ShopifyProductId.Trim(),
                ShopifyVariantId = (p.ShopifyVariantId ?? string.Empty).Trim(),
                ProductName = string.IsNullOrWhiteSpace( p.ProductName )
                    ? p.ShopifyProductId
                    : p.ProductName.Trim(),
                VatRatePercent = p.VatRatePercent,
                SupplierPrice = p.SupplierPrice
            } )
            .GroupBy(
                c => $"{c.ShopifyProductId}::{c.ShopifyVariantId}",
                StringComparer.OrdinalIgnoreCase )
            .Select( g => g.First() )
            .OrderBy( c => c.ProductName, StringComparer.OrdinalIgnoreCase )
            .ToList();

        await _groq.MatchProductsToCatalogAsync( result, candidates, cancellationToken );
        return result;
    }

    private static void AppendWarning( VatReportExpenseInvoiceExtractResult result, string warning )
    {
        if (string.IsNullOrWhiteSpace( warning ))
        {
            return;
        }

        result.Warning = string.IsNullOrWhiteSpace( result.Warning )
            ? warning
            : $"{result.Warning} {warning}";
    }

    private static Supplier? MatchSupplier( string vendorName, List<Supplier> suppliers )
    {
        string vendor = vendorName.Trim().ToLowerInvariant();
        if (vendor.Length == 0)
        {
            return null;
        }

        Supplier? exact = suppliers.FirstOrDefault( s =>
            string.Equals( s.Name.Trim(), vendorName.Trim(), StringComparison.OrdinalIgnoreCase ) );
        if (exact is not null)
        {
            return exact;
        }

        return suppliers
            .Select( s => new
            {
                Supplier = s,
                Name = s.Name.Trim().ToLowerInvariant()
            } )
            .Where( x => x.Name.Length > 0 && (x.Name.Contains( vendor ) || vendor.Contains( x.Name )) )
            .OrderByDescending( x => x.Name.Length )
            .Select( x => x.Supplier )
            .FirstOrDefault();
    }

    private static bool IsPdf( string fileName, string contentType, byte[] bytes )
    {
        if (!string.IsNullOrWhiteSpace( contentType )
            && contentType.Contains( "pdf", StringComparison.OrdinalIgnoreCase ))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace( fileName )
            && fileName.EndsWith( ".pdf", StringComparison.OrdinalIgnoreCase ))
        {
            return true;
        }

        return bytes.Length >= 4
            && bytes[0] == (byte)'%'
            && bytes[1] == (byte)'P'
            && bytes[2] == (byte)'D'
            && bytes[3] == (byte)'F';
    }
}
