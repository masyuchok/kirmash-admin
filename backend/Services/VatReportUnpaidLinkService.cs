using backend.Data;
using backend.Models;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public class VatReportUnpaidLinkService
{
    private readonly AppDbContext _db;
    private readonly VatReportLockService _locks;
    private readonly VatReportProfitService _profit;

    public VatReportUnpaidLinkService(
        AppDbContext db,
        VatReportLockService locks,
        VatReportProfitService profit )
    {
        _db = db;
        _locks = locks;
        _profit = profit;
    }

    public async Task<List<ProductOverpaidLineItem>> GetAllOverpaidLinesAsync()
    {
        List<int> supplierIds = await _db.VatReportExpenses
            .AsNoTracking()
            .Where( expense =>
                expense.SupplierId.HasValue &&
                (
                    expense.ExpenseInvoiceType.Name == ExpenseInvoiceTypeSeeder.SupplierPaymentDefaultName ||
                    expense.ExpenseInvoiceType.Name == ExpenseInvoiceTypeSeeder.LegacySupplierPaymentName
                ) )
            .Select( expense => expense.SupplierId!.Value )
            .Distinct()
            .ToListAsync();

        if (supplierIds.Count == 0)
        {
            return [];
        }

        Dictionary<int, string> supplierNames = await _db.Suppliers
            .AsNoTracking()
            .Where( supplier => supplier.Id.HasValue && supplierIds.Contains( supplier.Id.Value ) )
            .ToDictionaryAsync( supplier => supplier.Id!.Value, supplier => supplier.Name );

        Dictionary<string, int> totalPaidByLineKey = await LoadTotalPaidQuantityByLineKeyAsync();
        Dictionary<string, int> totalSoldByLineKey = await _profit.GetTotalSoldQuantityByLineKeyAsync();
        Dictionary<string, string> variantTitles = await _profit.GetVariantTitleLookupAsync();

        List<ProductOverpaidLineItem> result = new();
        foreach (int supplierId in supplierIds)
        {
            Dictionary<string, int> overpaidByLineKey = await BuildOverpaidQuantityByLineKeyAsync(
                supplierId,
                totalPaidByLineKey,
                totalSoldByLineKey );
            supplierNames.TryGetValue( supplierId, out string? supplierName );
            foreach (KeyValuePair<string, int> entry in overpaidByLineKey)
            {
                if (entry.Value <= 0 || string.IsNullOrWhiteSpace( entry.Key ))
                {
                    continue;
                }

                ParseProductLineKey( entry.Key, out string productId, out string variantId );
                result.Add( new ProductOverpaidLineItem
                {
                    SupplierId = supplierId,
                    SupplierName = supplierName ?? string.Empty,
                    ShopifyProductId = productId,
                    ShopifyVariantId = variantId,
                    ShopifyVariantTitle = variantTitles.GetValueOrDefault( variantId ) ?? string.Empty,
                    OverpaidQuantity = entry.Value
                } );
            }
        }

        return result;
    }

    public async Task<VatReportUnpaidLinkOptionsResponse> GetLinkOptionsAsync(
        int supplierId,
        int periodYear,
        int periodMonth,
        string shopifyProductId )
    {
        if (supplierId <= 0)
        {
            throw new InvalidOperationException( "Пастаўшчык не выбраны." );
        }

        Dictionary<string, int> overpaidByLineKey = await BuildOverpaidQuantityByLineKeyAsync( supplierId );
        List<SupplierPaymentLineRow> paymentLines = await LoadSupplierPaymentLinesAsync( supplierId );
        Dictionary<string, string> variantTitles = await _profit.GetVariantTitleLookupAsync();
        List<VatReportSupplierExpenseOption> supplierExpenses = await LoadSupplierExpenseOptionsAsync( supplierId );

        VatReportUnpaidLinkOptionsResponse response = new()
        {
            OverpaidProducts = paymentLines
                .Where( line => overpaidByLineKey.GetValueOrDefault( line.LineKey ) > 0 )
                .OrderByDescending( line => line.ExpenseDateUtc )
                .ThenBy( line => line.ExpenseProductId )
                .Select( line => new VatReportOverpaidExpenseProductOption
                {
                    ExpenseProductId = line.ExpenseProductId,
                    ExpenseId = line.ExpenseId,
                    ExpenseDateUtc = line.ExpenseDateUtc,
                    InvoiceNumber = line.InvoiceNumber,
                    Comment = line.Comment,
                    ProductTitle = line.ProductTitle,
                    ShopifyProductId = line.CatalogProductId,
                    ShopifyVariantId = line.VariantId,
                    ShopifyVariantTitle = variantTitles.GetValueOrDefault( line.VariantId ) ?? string.Empty,
                    Quantity = line.Quantity,
                    OverpaidQuantity = overpaidByLineKey.GetValueOrDefault( line.LineKey )
                } )
                .ToList(),
            SupplierInvoices = supplierExpenses
                .Where( option => option.HasInvoice )
                .ToList(),
            SupplierPaymentRecords = supplierExpenses
        };

        return response;
    }

    public async Task LinkUnpaidAsync( VatReportUnpaidLinkRequest request )
    {
        await EnsureSalePeriodUnlockedAsync( request.PeriodYear, request.PeriodMonth );

        if (request.SupplierId <= 0)
        {
            throw new InvalidOperationException( "Пастаўшчык не выбраны." );
        }
        if (request.Quantity <= 0)
        {
            throw new InvalidOperationException( "Колькасць павінна быць больш за 0." );
        }

        string unpaidProductId = ShopifyIds.NormalizeProductId( request.ShopifyProductId.Trim() );
        if (string.IsNullOrWhiteSpace( unpaidProductId ))
        {
            throw new InvalidOperationException( "Тавар не выбраны." );
        }

        string unpaidVariantId = NormalizeUnpaidVariantId( request.ShopifyVariantId );

        string mode = request.Mode.Trim().ToLowerInvariant();
        if (mode == "replace")
        {
            await ReplaceOverpaidProductAsync( request, unpaidProductId, unpaidVariantId );
            return;
        }

        if (mode == "link")
        {
            await LinkToSupplierPaymentAsync( request, unpaidProductId, unpaidVariantId );
            return;
        }

        throw new InvalidOperationException( "Невядомы рэжым прывязкі." );
    }

    private async Task ReplaceOverpaidProductAsync(
        VatReportUnpaidLinkRequest request,
        string unpaidProductId,
        string unpaidVariantId )
    {
        if (!request.ExpenseProductId.HasValue || request.ExpenseProductId.Value <= 0)
        {
            throw new InvalidOperationException( "Выберыце пераплачаны тавар у фактуре." );
        }

        VatReportExpenseProduct? expenseProduct = await _db.VatReportExpenseProducts
            .Include( p => p.VatReportExpense )
            .ThenInclude( e => e.ExpenseInvoiceType )
            .FirstOrDefaultAsync( p => p.Id == request.ExpenseProductId.Value );
        if (expenseProduct is null)
        {
            throw new InvalidOperationException( "Радок фактуры не знойдзены." );
        }

        ValidateSupplierPaymentExpense( expenseProduct.VatReportExpense, request.SupplierId );

        QuantityLineKeyMaps lineKeyMaps = await _profit.GetQuantityLineKeyMapsAsync();
        string expenseLineKey = VatReportProfitService.ResolveQuantityLineKey(
            expenseProduct.ShopifyProductId,
            expenseProduct.ShopifyVariantId,
            string.Empty,
            expenseProduct.ProductTitle,
            lineKeyMaps );
        Dictionary<string, int> overpaidByLineKey = await BuildOverpaidQuantityByLineKeyAsync( request.SupplierId );
        if (!overpaidByLineKey.TryGetValue( expenseLineKey, out int overpaidQty ) || overpaidQty <= 0)
        {
            throw new InvalidOperationException( "Гэты тавар у фактуре не з'яўляецца пераплачаным." );
        }

        if (request.Quantity > expenseProduct.Quantity)
        {
            throw new InvalidOperationException( "Колькасць неаплачанага тавару перавышае колькасць у радку фактуры." );
        }

        if (request.Quantity > overpaidQty)
        {
            throw new InvalidOperationException(
                "Колькасць замены перавышае пераплату па гэтым тавары." );
        }

        string unpaidTitle = string.IsNullOrWhiteSpace( request.ProductTitle )
            ? unpaidProductId
            : request.ProductTitle.Trim();
        if (string.IsNullOrWhiteSpace( unpaidVariantId ))
        {
            unpaidVariantId = await ResolveVariantIdForProductAsync( unpaidProductId );
        }

        if (string.IsNullOrWhiteSpace( unpaidVariantId ) &&
            !string.IsNullOrWhiteSpace( expenseProduct.ShopifyVariantId ))
        {
            unpaidVariantId = NormalizeUnpaidVariantId( expenseProduct.ShopifyVariantId );
        }

        unpaidProductId = ShopifyIds.NormalizeProductId( unpaidProductId );
        unpaidVariantId = NormalizeUnpaidVariantId( unpaidVariantId );

        if (request.Quantity < expenseProduct.Quantity)
        {
            expenseProduct.Quantity -= request.Quantity;
            _db.VatReportExpenseProducts.Add( new VatReportExpenseProduct
            {
                VatReportExpenseId = expenseProduct.VatReportExpenseId,
                ShopifyProductId = unpaidProductId,
                ShopifyVariantId = unpaidVariantId,
                ProductTitle = unpaidTitle,
                Quantity = request.Quantity,
                UnitGrossPrice = expenseProduct.UnitGrossPrice
            } );
        }
        else
        {
            expenseProduct.ShopifyProductId = unpaidProductId;
            expenseProduct.ShopifyVariantId = unpaidVariantId;
            expenseProduct.ProductTitle = unpaidTitle;
        }

        await _db.SaveChangesAsync();

        await UpsertUnpaidAllocationAsync(
            request,
            unpaidProductId,
            unpaidVariantId,
            unpaidTitle,
            expenseProduct.VatReportExpenseId );

        await _db.SaveChangesAsync();
    }

    private async Task LinkToSupplierPaymentAsync(
        VatReportUnpaidLinkRequest request,
        string unpaidProductId,
        string unpaidVariantId )
    {
        if (!request.ExpenseId.HasValue || request.ExpenseId.Value <= 0)
        {
            throw new InvalidOperationException( "Выберыце фактуру або запіс аплаты." );
        }

        VatReportExpense? expense = await _db.VatReportExpenses
            .Include( e => e.ExpenseInvoiceType )
            .Include( e => e.Products )
            .FirstOrDefaultAsync( e => e.Id == request.ExpenseId.Value );
        if (expense is null)
        {
            throw new InvalidOperationException( "Запіс аплаты не знойдзены." );
        }

        ValidateSupplierPaymentExpense( expense, request.SupplierId );
        await _locks.EnsurePeriodUnlockedByExpenseIdAsync( expense.Id );

        if (string.IsNullOrWhiteSpace( unpaidVariantId ))
        {
            unpaidVariantId = await ResolveVariantIdForProductAsync( unpaidProductId );
        }

        unpaidProductId = ShopifyIds.NormalizeProductId( unpaidProductId );
        unpaidVariantId = NormalizeUnpaidVariantId( unpaidVariantId );

        bool exists = await _db.VatReportUnpaidAllocations.AnyAsync( allocation =>
            allocation.SalePeriodYear == request.PeriodYear &&
            allocation.SalePeriodMonth == request.PeriodMonth &&
            allocation.ShopifyProductId == unpaidProductId &&
            allocation.ShopifyVariantId == unpaidVariantId &&
            allocation.SupplierId == request.SupplierId );
        if (exists)
        {
            throw new InvalidOperationException( "Гэты неаплачаны тавар ужо прывязаны да аплаты." );
        }

        string unpaidTitle = string.IsNullOrWhiteSpace( request.ProductTitle )
            ? unpaidProductId
            : request.ProductTitle.Trim();

        if (expense.Products.Count == 0)
        {
            decimal unitPrice = await ResolveSupplyUnitPriceAsync( request.SupplierId, unpaidProductId );
            expense.Products.Add( new VatReportExpenseProduct
            {
                ShopifyProductId = unpaidProductId,
                ShopifyVariantId = unpaidVariantId,
                ProductTitle = unpaidTitle,
                Quantity = request.Quantity,
                UnitGrossPrice = unitPrice
            } );
        }

        await UpsertUnpaidAllocationAsync(
            request,
            unpaidProductId,
            unpaidVariantId,
            unpaidTitle,
            expense.Id );

        await _db.SaveChangesAsync();
    }

    private async Task UpsertUnpaidAllocationAsync(
        VatReportUnpaidLinkRequest request,
        string unpaidProductId,
        string unpaidVariantId,
        string unpaidTitle,
        int expenseId )
    {
        unpaidProductId = ShopifyIds.NormalizeProductId( unpaidProductId );
        unpaidVariantId = NormalizeUnpaidVariantId( unpaidVariantId );

        VatReportUnpaidAllocation? existing = await _db.VatReportUnpaidAllocations.FirstOrDefaultAsync( allocation =>
            allocation.SalePeriodYear == request.PeriodYear &&
            allocation.SalePeriodMonth == request.PeriodMonth &&
            allocation.ShopifyProductId == unpaidProductId &&
            allocation.ShopifyVariantId == unpaidVariantId &&
            allocation.SupplierId == request.SupplierId );
        if (existing is not null)
        {
            existing.Quantity += request.Quantity;
            existing.VatReportExpenseId = expenseId;
            existing.ProductTitle = unpaidTitle;
            return;
        }

        _db.VatReportUnpaidAllocations.Add( new VatReportUnpaidAllocation
        {
            SalePeriodYear = request.PeriodYear,
            SalePeriodMonth = request.PeriodMonth,
            ShopifyProductId = unpaidProductId,
            ShopifyVariantId = unpaidVariantId,
            ProductTitle = unpaidTitle,
            SupplierId = request.SupplierId,
            Quantity = request.Quantity,
            VatReportExpenseId = expenseId,
            CreatedAtUtc = DateTime.UtcNow
        } );
    }

    private async Task<decimal> ResolveSupplyUnitPriceAsync( int supplierId, string productId )
    {
        SupplyProduct? supplyProduct = await _db.SupplyProducts
            .AsNoTracking()
            .Where( sp =>
                sp.Supply.SupplierId == supplierId &&
                sp.SupplierPrice > 0m &&
                sp.ShopifyProductId == productId )
            .OrderByDescending( sp => sp.Supply.Date )
            .ThenByDescending( sp => sp.SupplyId )
            .FirstOrDefaultAsync();
        return supplyProduct?.SupplierPrice ?? 0m;
    }

    private async Task<List<VatReportSupplierExpenseOption>> LoadSupplierExpenseOptionsAsync( int supplierId )
    {
        List<SupplierExpenseRow> rows = await _db.VatReportExpenses
            .AsNoTracking()
            .Where( expense =>
                expense.SupplierId == supplierId &&
                (
                    expense.ExpenseInvoiceType.Name == ExpenseInvoiceTypeSeeder.SupplierPaymentDefaultName ||
                    expense.ExpenseInvoiceType.Name == ExpenseInvoiceTypeSeeder.LegacySupplierPaymentName
                ) )
            .Select( expense => new SupplierExpenseRow
            {
                ExpenseId = expense.Id,
                ExpenseDateUtc = expense.ExpenseDateUtc,
                InvoiceNumber = expense.InvoiceNumber,
                Comment = expense.Comment ?? string.Empty,
                ExpenseInvoiceTypeName = expense.ExpenseInvoiceType.Name,
                GrossAmount = expense.GrossAmount,
                InvoiceFileName = expense.InvoiceFileName,
                TotalProductUnits = expense.Products.Sum( product => product.Quantity )
            } )
            .ToListAsync();

        return rows
            .Select( row => new VatReportSupplierExpenseOption
            {
                ExpenseId = row.ExpenseId,
                ExpenseDateUtc = row.ExpenseDateUtc,
                InvoiceNumber = row.InvoiceNumber,
                Comment = row.Comment,
                ExpenseInvoiceTypeName = row.ExpenseInvoiceTypeName,
                GrossAmount = row.GrossAmount,
                TotalProductUnits = row.TotalProductUnits,
                HasInvoice = HasInvoiceDocument( row.InvoiceFileName, row.InvoiceNumber )
            } )
            .OrderByDescending( option => option.ExpenseDateUtc )
            .ThenByDescending( option => option.ExpenseId )
            .ToList();
    }

    private static bool HasInvoiceDocument( string invoiceFileName, string invoiceNumber ) =>
        !string.IsNullOrWhiteSpace( invoiceFileName ) || !string.IsNullOrWhiteSpace( invoiceNumber );

    private async Task EnsureSalePeriodUnlockedAsync( int periodYear, int periodMonth )
    {
        VatReport? report = await _db.VatReports.FirstOrDefaultAsync( report =>
            report.PeriodYear == periodYear &&
            report.PeriodMonth == periodMonth &&
            report.Type == VatReportType.Poland );
        if (report is null)
        {
            throw new InvalidOperationException( "Польская справаздача за гэты перыяд не знойдзена." );
        }

        await _locks.EnsurePeriodUnlockedByReportIdAsync( report.Id );
    }

    private static void ValidateSupplierPaymentExpense( VatReportExpense expense, int supplierId )
    {
        if (!IsSupplierPaymentType( expense.ExpenseInvoiceType.Name ))
        {
            throw new InvalidOperationException( "Можна выбраць толькі аплату пастаўшчыку." );
        }

        if (!expense.SupplierId.HasValue || expense.SupplierId.Value != supplierId)
        {
            throw new InvalidOperationException( "Фактура адносіцца да іншага пастаўшчыка." );
        }
    }

    private static bool IsSupplierPaymentType( string typeName ) =>
        string.Equals( typeName, ExpenseInvoiceTypeSeeder.SupplierPaymentDefaultName, StringComparison.Ordinal ) ||
        string.Equals( typeName, ExpenseInvoiceTypeSeeder.LegacySupplierPaymentName, StringComparison.Ordinal );

    private static void ParseProductLineKey( string lineKey, out string productId, out string variantId ) =>
        VatReportHelpers.ParseProductLineKey( lineKey, out productId, out variantId );

    private async Task<Dictionary<string, int>> BuildOverpaidQuantityByLineKeyAsync(
        int supplierId,
        IReadOnlyDictionary<string, int>? totalPaidByLineKey = null,
        IReadOnlyDictionary<string, int>? totalSoldByLineKey = null )
    {
        Dictionary<string, int> paid = new( StringComparer.OrdinalIgnoreCase );
        List<SupplierPaymentLineRow> paymentLines = await LoadSupplierPaymentLinesAsync( supplierId );
        foreach (SupplierPaymentLineRow line in paymentLines)
        {
            if (string.IsNullOrWhiteSpace( line.LineKey )) continue;
            paid[line.LineKey] = paid.GetValueOrDefault( line.LineKey ) + line.Quantity;
        }

        Dictionary<string, int> sold = await _profit.GetSoldQuantityBySupplierLineKeyAsync( supplierId );
        totalPaidByLineKey ??= await LoadTotalPaidQuantityByLineKeyAsync();
        totalSoldByLineKey ??= await _profit.GetTotalSoldQuantityByLineKeyAsync();

        Dictionary<string, int> overpaid = new( StringComparer.OrdinalIgnoreCase );
        foreach (KeyValuePair<string, int> entry in paid)
        {
            int globalSurplus = totalPaidByLineKey.GetValueOrDefault( entry.Key ) -
                                totalSoldByLineKey.GetValueOrDefault( entry.Key );
            if (globalSurplus <= 0)
            {
                continue;
            }

            int supplierSurplus = entry.Value - sold.GetValueOrDefault( entry.Key );
            if (supplierSurplus <= 0)
            {
                continue;
            }

            overpaid[entry.Key] = Math.Min( supplierSurplus, globalSurplus );
        }

        List<VatReportUnpaidAllocation> linkedUnpaid = await _db.VatReportUnpaidAllocations
            .AsNoTracking()
            .Where( allocation => allocation.SupplierId == supplierId )
            .ToListAsync();
        SubtractLinkedUnpaidFromOverpaid( overpaid, linkedUnpaid );

        return overpaid;
    }

    private static void SubtractLinkedUnpaidFromOverpaid(
        Dictionary<string, int> overpaid,
        IReadOnlyList<VatReportUnpaidAllocation> linkedUnpaid )
    {
        foreach (VatReportUnpaidAllocation link in linkedUnpaid)
        {
            int remaining = link.Quantity;
            if (remaining <= 0)
            {
                continue;
            }

            foreach (KeyValuePair<string, int> entry in overpaid.ToList())
            {
                if (remaining <= 0)
                {
                    break;
                }

                VatReportHelpers.ParseProductLineKey( entry.Key, out string paidProductId, out string paidVariantId );
                if (!VatReportHelpers.ProductLineKeysEqual(
                        link.ShopifyProductId,
                        link.ShopifyVariantId,
                        paidProductId,
                        paidVariantId ))
                {
                    continue;
                }

                int deduct = Math.Min( remaining, entry.Value );
                if (deduct <= 0)
                {
                    continue;
                }

                int next = entry.Value - deduct;
                if (next <= 0)
                {
                    overpaid.Remove( entry.Key );
                }
                else
                {
                    overpaid[entry.Key] = next;
                }

                remaining -= deduct;
            }
        }
    }

    private static string NormalizeUnpaidVariantId( string? raw ) =>
        string.IsNullOrWhiteSpace( raw ) ? string.Empty : ShopifyIds.NormalizeVariantId( raw.Trim() );

    private async Task<Dictionary<string, int>> LoadTotalPaidQuantityByLineKeyAsync()
    {
        QuantityLineKeyMaps lineKeyMaps = await _profit.GetQuantityLineKeyMapsAsync();
        var rows = await _db.VatReportExpenseProducts
            .AsNoTracking()
            .Where( p =>
                p.Quantity > 0 &&
                p.VatReportExpense.SupplierId.HasValue &&
                (
                    p.VatReportExpense.ExpenseInvoiceType.Name == ExpenseInvoiceTypeSeeder.SupplierPaymentDefaultName ||
                    p.VatReportExpense.ExpenseInvoiceType.Name == ExpenseInvoiceTypeSeeder.LegacySupplierPaymentName
                ) )
            .Select( p => new
            {
                p.ShopifyProductId,
                p.ShopifyVariantId,
                p.ProductTitle,
                p.Quantity
            } )
            .ToListAsync();

        Dictionary<string, int> paid = new( StringComparer.OrdinalIgnoreCase );
        foreach (var row in rows)
        {
            string lineKey = VatReportProfitService.ResolveQuantityLineKey(
                row.ShopifyProductId,
                row.ShopifyVariantId,
                string.Empty,
                row.ProductTitle,
                lineKeyMaps );
            if (string.IsNullOrWhiteSpace( lineKey )) continue;
            paid[lineKey] = paid.GetValueOrDefault( lineKey ) + row.Quantity;
        }

        return paid;
    }

    private async Task<List<SupplierPaymentLineRow>> LoadSupplierPaymentLinesAsync( int supplierId )
    {
        QuantityLineKeyMaps lineKeyMaps = await _profit.GetQuantityLineKeyMapsAsync();
        List<SupplierPaymentLineRow> rows = await _db.VatReportExpenseProducts
            .AsNoTracking()
            .Where( p =>
                p.Quantity > 0 &&
                p.VatReportExpense.SupplierId == supplierId &&
                (
                    p.VatReportExpense.ExpenseInvoiceType.Name == ExpenseInvoiceTypeSeeder.SupplierPaymentDefaultName ||
                    p.VatReportExpense.ExpenseInvoiceType.Name == ExpenseInvoiceTypeSeeder.LegacySupplierPaymentName
                ) )
            .Select( p => new SupplierPaymentLineRow
            {
                ExpenseProductId = p.Id,
                ExpenseId = p.VatReportExpenseId,
                ExpenseDateUtc = p.VatReportExpense.ExpenseDateUtc,
                InvoiceNumber = p.VatReportExpense.InvoiceNumber,
                Comment = p.VatReportExpense.Comment ?? string.Empty,
                GrossAmount = p.VatReportExpense.GrossAmount,
                ProductId = p.ShopifyProductId,
                VariantId = p.ShopifyVariantId,
                ProductTitle = p.ProductTitle,
                Quantity = p.Quantity
            } )
            .ToListAsync();

        foreach (SupplierPaymentLineRow row in rows)
        {
            row.LineKey = VatReportProfitService.ResolveQuantityLineKey(
                row.ProductId,
                row.VariantId,
                string.Empty,
                row.ProductTitle,
                lineKeyMaps );
            VatReportHelpers.ParseProductLineKey( row.LineKey, out string catalogProductId, out string variantId );
            row.CatalogProductId = catalogProductId;
            row.VariantId = variantId;
        }

        return rows;
    }

    private async Task<Dictionary<string, string>> LoadVariantToProductMapAsync()
    {
        Dictionary<string, string> map = new( StringComparer.OrdinalIgnoreCase );

        void AddMapping( string? variantRaw, string? productRaw )
        {
            string variantId = ShopifyIds.NormalizeVariantId( variantRaw ?? string.Empty );
            string productId = ShopifyIds.NormalizeProductId( productRaw ?? string.Empty );
            if (string.IsNullOrWhiteSpace( variantId ) || string.IsNullOrWhiteSpace( productId ))
            {
                return;
            }

            map[variantId] = productId;
        }

        foreach (var row in await _db.SupplyProducts.AsNoTracking()
                     .Where( sp => sp.ShopifyVariantId != "" && sp.ShopifyProductId != "" )
                     .Select( sp => new { sp.ShopifyVariantId, sp.ShopifyProductId } )
                     .ToListAsync())
        {
            AddMapping( row.ShopifyVariantId, row.ShopifyProductId );
        }

        foreach (var row in await _db.VatReportExpenseProducts.AsNoTracking()
                     .Where( p => p.ShopifyVariantId != "" && p.ShopifyProductId != "" )
                     .Select( p => new { p.ShopifyVariantId, p.ShopifyProductId } )
                     .ToListAsync())
        {
            AddMapping( row.ShopifyVariantId, row.ShopifyProductId );
        }

        foreach (var row in await _db.VatReportCashSales.AsNoTracking()
                     .Where( x => x.ShopifyVariantId != "" && x.ShopifyProductId != "" )
                     .Select( x => new { x.ShopifyVariantId, x.ShopifyProductId } )
                     .ToListAsync())
        {
            AddMapping( row.ShopifyVariantId, row.ShopifyProductId );
        }

        return map;
    }

    private static string ResolveCatalogProductId(
        string productRaw,
        string variantRaw,
        IReadOnlyDictionary<string, string> variantToProduct )
    {
        string variantMapped = ResolveVariantMappedProductId( variantRaw, variantToProduct );
        if (!string.IsNullOrWhiteSpace( variantMapped ))
        {
            return variantMapped;
        }

        string variantAsProduct = ResolveVariantMappedProductId( productRaw, variantToProduct );
        if (!string.IsNullOrWhiteSpace( variantAsProduct ))
        {
            return variantAsProduct;
        }

        return ShopifyIds.NormalizeProductId( productRaw );
    }

    private static string ResolveVariantMappedProductId(
        string raw,
        IReadOnlyDictionary<string, string> variantToProduct )
    {
        string variantId = ShopifyIds.NormalizeVariantId( raw );
        if (string.IsNullOrWhiteSpace( variantId ))
        {
            return string.Empty;
        }

        return variantToProduct.TryGetValue( variantId, out string? productId ) ? productId : string.Empty;
    }

    private async Task<string> ResolveVariantIdForProductAsync( string productId )
    {
        string? variantId = await _db.SupplyProducts
            .AsNoTracking()
            .Where( sp => sp.ShopifyProductId == productId && sp.ShopifyVariantId != "" )
            .OrderByDescending( sp => sp.Supply.Date )
            .Select( sp => sp.ShopifyVariantId )
            .FirstOrDefaultAsync();
        return variantId ?? string.Empty;
    }

    private sealed class SupplierPaymentLineRow
    {
        public int ExpenseProductId { get; set; }
        public int ExpenseId { get; set; }
        public DateTime ExpenseDateUtc { get; set; }
        public string InvoiceNumber { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public decimal GrossAmount { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public string VariantId { get; set; } = string.Empty;
        public string ProductTitle { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public string CatalogProductId { get; set; } = string.Empty;
        public string LineKey { get; set; } = string.Empty;
    }

    private sealed class SupplierExpenseRow
    {
        public int ExpenseId { get; set; }
        public DateTime ExpenseDateUtc { get; set; }
        public string InvoiceNumber { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public string ExpenseInvoiceTypeName { get; set; } = string.Empty;
        public decimal GrossAmount { get; set; }
        public string InvoiceFileName { get; set; } = string.Empty;
        public int TotalProductUnits { get; set; }
    }
}
