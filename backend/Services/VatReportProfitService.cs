using backend.Data;
using backend.Models;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services;

public class VatReportProfitService
{
    private readonly AppDbContext _db;

    public VatReportProfitService( AppDbContext db )
    {
        _db = db;
    }

    public async Task<decimal> ComputePeriodProfitAsync(
        int periodYear,
        int periodMonth,
        IEnumerable<VatReportDetailsSummaryRow> summaryRows )
    {
        decimal revenue = summaryRows
            .Where( r =>
                r.Type == VatReportType.Poland ||
                r.Type == VatReportType.Foreign ||
                r.Type == VatReportType.Cash )
            .Sum( r => r.GrossAmount );

        decimal nonSupplierExpenseGross = await SumNonSupplierExpenseGrossAsync( periodYear, periodMonth );
        decimal cogs = await ComputePeriodCogsAsync( periodYear, periodMonth );

        return VatReportHelpers.Round2( revenue - nonSupplierExpenseGross - cogs );
    }

    private async Task<decimal> SumNonSupplierExpenseGrossAsync( int periodYear, int periodMonth )
    {
        int? polandReportId = await _db.VatReports
            .AsNoTracking()
            .Where( r =>
                r.PeriodYear == periodYear &&
                r.PeriodMonth == periodMonth &&
                r.Type == VatReportType.Poland )
            .Select( r => (int?)r.Id )
            .FirstOrDefaultAsync();

        if (!polandReportId.HasValue)
        {
            return 0m;
        }

        List<ExpenseGrossRow> expenses = await _db.VatReportExpenses
            .AsNoTracking()
            .Where( e => e.VatReportId == polandReportId.Value )
            .Select( e => new ExpenseGrossRow
            {
                GrossAmount = e.GrossAmount,
                TypeName = e.ExpenseInvoiceType.Name
            } )
            .ToListAsync();

        return expenses
            .Where( e => !IsSupplierPaymentType( e.TypeName ) )
            .Sum( e => e.GrossAmount );
    }

    public async Task<decimal> ComputePeriodCogsAsync( int periodYear, int periodMonth )
    {
        Dictionary<(int Year, int Month), decimal> cogsByPeriod = await BuildCogsByPeriodAsync();
        return cogsByPeriod.GetValueOrDefault( (periodYear, periodMonth) );
    }

    private async Task<Dictionary<(int Year, int Month), decimal>> BuildCogsByPeriodAsync()
    {
        List<SaleUnit> saleUnits = await LoadSaleUnitsAsync();
        List<PaymentUnit> paymentUnits = await LoadPaymentUnitsAsync();

        Dictionary<(int Year, int Month), decimal> cogsByPeriod = new();
        Queue<PendingSaleUnit> pending = new();
        List<PaymentUnit> availablePayments = paymentUnits
            .Select( p => new PaymentUnit
            {
                Id = p.Id,
                DateUtc = p.DateUtc,
                ProductId = p.ProductId,
                SupplierId = p.SupplierId,
                UnitGrossPrice = p.UnitGrossPrice,
                Remaining = p.Remaining
            } )
            .ToList();

        void AddCogs( int year, int month, decimal amount )
        {
            if (amount <= 0m) return;
            (int Year, int Month) key = (year, month);
            cogsByPeriod[key] = cogsByPeriod.GetValueOrDefault( key ) + amount;
        }

        decimal AllocateFromPayments( string productId, int? supplierId, int quantity )
        {
            decimal allocatedCost = 0m;
            int remaining = quantity;

            IEnumerable<PaymentUnit> candidates = availablePayments
                .Where( p => p.Remaining > 0 && ProductIdsEqual( p.ProductId, productId ) )
                .OrderBy( p => p.DateUtc )
                .ThenBy( p => p.Id );

            if (supplierId.HasValue && supplierId.Value > 0)
            {
                candidates = candidates
                    .OrderBy( p => p.SupplierId == supplierId.Value ? 0 : 1 )
                    .ThenBy( p => p.DateUtc )
                    .ThenBy( p => p.Id );
            }

            foreach (PaymentUnit payment in candidates.ToList())
            {
                if (remaining <= 0) break;
                if (!ProductIdsEqual( payment.ProductId, productId )) continue;

                int take = Math.Min( remaining, payment.Remaining );
                if (take <= 0) continue;

                payment.Remaining -= take;
                remaining -= take;
                allocatedCost += take * payment.UnitGrossPrice;
            }

            return allocatedCost;
        }

        void FulfillPending()
        {
            int pendingCount = pending.Count;
            for (int i = 0; i < pendingCount; i++)
            {
                PendingSaleUnit sale = pending.Dequeue();
                decimal cost = AllocateFromPayments( sale.ProductId, sale.SupplierId, 1 );
                if (cost > 0m)
                {
                    AddCogs( sale.PeriodYear, sale.PeriodMonth, cost );
                }
                else
                {
                    pending.Enqueue( sale );
                }
            }
        }

        List<TimelineEvent> timeline = new();
        foreach (SaleUnit sale in saleUnits)
        {
            timeline.Add( new TimelineEvent
            {
                DateUtc = sale.DateUtc,
                SortKey = sale.Id,
                Kind = TimelineEventKind.Sale,
                Sale = sale
            } );
        }

        foreach (PaymentUnit payment in paymentUnits)
        {
            timeline.Add( new TimelineEvent
            {
                DateUtc = payment.DateUtc,
                SortKey = payment.Id,
                Kind = TimelineEventKind.Payment,
                Payment = payment
            } );
        }

        foreach (TimelineEvent entry in timeline.OrderBy( e => e.DateUtc ).ThenBy( e => e.Kind ).ThenBy( e => e.SortKey ))
        {
            if (entry.Kind == TimelineEventKind.Payment)
            {
                FulfillPending();
                continue;
            }

            SaleUnit sale = entry.Sale!;
            for (int i = 0; i < sale.Quantity; i++)
            {
                decimal cost = AllocateFromPayments( sale.ProductId, sale.SupplierId, 1 );
                if (cost > 0m)
                {
                    AddCogs( sale.PeriodYear, sale.PeriodMonth, cost );
                }
                else
                {
                    pending.Enqueue( new PendingSaleUnit
                    {
                        PeriodYear = sale.PeriodYear,
                        PeriodMonth = sale.PeriodMonth,
                        ProductId = sale.ProductId,
                        SupplierId = sale.SupplierId
                    } );
                }
            }
        }

        FulfillPending();

        foreach (KeyValuePair<(int Year, int Month), decimal> entry in cogsByPeriod.ToList())
        {
            cogsByPeriod[entry.Key] = VatReportHelpers.Round2( entry.Value );
        }

        return cogsByPeriod;
    }

    private async Task<List<SaleUnit>> LoadSaleUnitsAsync()
    {
        List<SaleUnit> sales = new();

        List<RowSaleRow> rowSales = await _db.VatReportRowItems
            .AsNoTracking()
            .Where( i =>
                i.Quantity > 0 &&
                !string.IsNullOrWhiteSpace( i.ShopifyProductId ) &&
                (i.VatReportRow.VatReport.Type == VatReportType.Poland ||
                 i.VatReportRow.VatReport.Type == VatReportType.Foreign) )
            .Select( i => new RowSaleRow
            {
                Id = i.Id,
                ProductId = i.ShopifyProductId,
                Quantity = i.Quantity,
                OrderDateUtc = i.VatReportRow.OrderDateUtc,
                PeriodYear = i.VatReportRow.VatReport.PeriodYear,
                PeriodMonth = i.VatReportRow.VatReport.PeriodMonth
            } )
            .ToListAsync();

        foreach (RowSaleRow row in rowSales)
        {
            sales.Add( new SaleUnit
            {
                Id = row.Id,
                ProductId = NormalizeProductId( row.ProductId ),
                Quantity = row.Quantity,
                DateUtc = row.OrderDateUtc,
                PeriodYear = row.PeriodYear,
                PeriodMonth = row.PeriodMonth,
                SupplierId = null
            } );
        }

        List<CashSaleRow> cashSales = await _db.VatReportCashSales
            .AsNoTracking()
            .Where( x => x.Quantity > 0 && !string.IsNullOrWhiteSpace( x.ShopifyProductId ) )
            .Select( x => new CashSaleRow
            {
                Id = x.Id,
                ProductId = x.ShopifyProductId,
                Quantity = x.Quantity,
                CreatedAtUtc = x.CreatedAtUtc,
                PeriodYear = x.VatReport.PeriodYear,
                PeriodMonth = x.VatReport.PeriodMonth
            } )
            .ToListAsync();

        foreach (CashSaleRow row in cashSales)
        {
            sales.Add( new SaleUnit
            {
                Id = 1_000_000 + row.Id,
                ProductId = NormalizeProductId( row.ProductId ),
                Quantity = row.Quantity,
                DateUtc = row.CreatedAtUtc,
                PeriodYear = row.PeriodYear,
                PeriodMonth = row.PeriodMonth,
                SupplierId = null
            } );
        }

        Dictionary<string, int> soldByProduct = sales
            .GroupBy( s => s.ProductId, StringComparer.OrdinalIgnoreCase )
            .ToDictionary( g => g.Key, g => g.Sum( x => x.Quantity ), StringComparer.OrdinalIgnoreCase );

        Dictionary<(int SupplierId, string ProductId), int> soldBySupplierProduct =
            await BuildSoldBySupplierProductAsync( soldByProduct );

        foreach (SaleUnit sale in sales)
        {
            KeyValuePair<(int SupplierId, string ProductId), int> match = soldBySupplierProduct
                .Where( x => ProductIdsEqual( x.Key.ProductId, sale.ProductId ) && x.Value > 0 )
                .OrderByDescending( x => x.Value )
                .FirstOrDefault();

            if (match.Value > 0)
            {
                sale.SupplierId = match.Key.SupplierId;
            }
        }

        return sales;
    }

    private async Task<Dictionary<(int SupplierId, string ProductId), int>> BuildSoldBySupplierProductAsync(
        Dictionary<string, int> soldByProduct )
    {
        List<SupplyBatchRow> supplyBatches = await _db.SupplyProducts
            .AsNoTracking()
            .Select( sp => new SupplyBatchRow
            {
                SupplierId = sp.Supply.SupplierId,
                ProductId = sp.ShopifyProductId,
                Quantity = sp.Quantity,
                SupplyDate = sp.Supply.Date
            } )
            .ToListAsync();

        supplyBatches = supplyBatches
            .Select( b =>
            {
                b.ProductId = NormalizeProductId( b.ProductId );
                return b;
            } )
            .OrderBy( b => b.SupplyDate )
            .ThenBy( b => b.SupplierId )
            .ToList();

        Dictionary<(int SupplierId, string ProductId), int> soldBySupplierProduct = new();

        foreach (string productId in soldByProduct.Keys)
        {
            int remainingSold = soldByProduct.GetValueOrDefault( productId );
            if (remainingSold <= 0) continue;

            foreach (SupplyBatchRow batch in supplyBatches.Where( b => ProductIdsEqual( b.ProductId, productId ) ))
            {
                if (remainingSold <= 0) break;
                int allocated = Math.Min( remainingSold, Math.Max( 0, batch.Quantity ) );
                if (allocated <= 0) continue;

                (int SupplierId, string ProductId) key = (batch.SupplierId, productId);
                soldBySupplierProduct[key] = soldBySupplierProduct.GetValueOrDefault( key ) + allocated;
                remainingSold -= allocated;
            }
        }

        return soldBySupplierProduct;
    }

    private async Task<List<PaymentUnit>> LoadPaymentUnitsAsync()
    {
        List<PaymentUnit> units = new();
        List<PaymentProductRow> rows = await _db.VatReportExpenseProducts
            .AsNoTracking()
            .Where( p =>
                p.Quantity > 0 &&
                p.UnitGrossPrice > 0m &&
                p.VatReportExpense.ExpenseInvoiceType.Name == ExpenseInvoiceTypeSeeder.SupplierPaymentDefaultName )
            .Select( p => new PaymentProductRow
            {
                Id = p.Id,
                ProductId = p.ShopifyProductId,
                Quantity = p.Quantity,
                UnitGrossPrice = p.UnitGrossPrice,
                DateUtc = p.VatReportExpense.ExpenseDateUtc,
                SupplierId = p.VatReportExpense.SupplierId
            } )
            .OrderBy( p => p.DateUtc )
            .ThenBy( p => p.Id )
            .ToListAsync();

        foreach (PaymentProductRow row in rows)
        {
            units.Add( new PaymentUnit
            {
                Id = row.Id,
                ProductId = NormalizeProductId( row.ProductId ),
                Quantity = row.Quantity,
                Remaining = row.Quantity,
                UnitGrossPrice = row.UnitGrossPrice,
                DateUtc = row.DateUtc,
                SupplierId = row.SupplierId
            } );
        }

        return units;
    }

    private static bool IsSupplierPaymentType( string typeName ) =>
        string.Equals( typeName, ExpenseInvoiceTypeSeeder.SupplierPaymentDefaultName, StringComparison.Ordinal ) ||
        string.Equals( typeName, ExpenseInvoiceTypeSeeder.LegacySupplierPaymentName, StringComparison.Ordinal );

    private static string NormalizeProductId( string raw ) =>
        ShopifyIds.NormalizeGid( raw?.Trim() ?? string.Empty, "gid://shopify/Product/" ).Trim();

    private static bool ProductIdsEqual( string left, string right ) =>
        string.Equals( NormalizeProductId( left ), NormalizeProductId( right ), StringComparison.OrdinalIgnoreCase );

    private enum TimelineEventKind
    {
        Payment = 0,
        Sale = 1
    }

    private sealed class TimelineEvent
    {
        public DateTime DateUtc { get; set; }
        public int SortKey { get; set; }
        public TimelineEventKind Kind { get; set; }
        public SaleUnit? Sale { get; set; }
        public PaymentUnit? Payment { get; set; }
    }

    private sealed class SaleUnit
    {
        public int Id { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public DateTime DateUtc { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
        public int? SupplierId { get; set; }
    }

    private sealed class PendingSaleUnit
    {
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public int? SupplierId { get; set; }
    }

    private sealed class PaymentUnit
    {
        public int Id { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public int Remaining { get; set; }
        public decimal UnitGrossPrice { get; set; }
        public DateTime DateUtc { get; set; }
        public int? SupplierId { get; set; }
    }

    private sealed class ExpenseGrossRow
    {
        public decimal GrossAmount { get; set; }
        public string TypeName { get; set; } = string.Empty;
    }

    private sealed class RowSaleRow
    {
        public int Id { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public DateTime OrderDateUtc { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
    }

    private sealed class CashSaleRow
    {
        public int Id { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public int PeriodYear { get; set; }
        public int PeriodMonth { get; set; }
    }

    private sealed class PaymentProductRow
    {
        public int Id { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitGrossPrice { get; set; }
        public DateTime DateUtc { get; set; }
        public int? SupplierId { get; set; }
    }

    private sealed class SupplyBatchRow
    {
        public int SupplierId { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public DateOnly SupplyDate { get; set; }
    }
}
