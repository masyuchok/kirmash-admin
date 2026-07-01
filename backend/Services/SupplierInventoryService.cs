using backend.Data;
using backend.Models;
using backend.Services.Shopify;
using Microsoft.EntityFrameworkCore;

namespace backend.Services
{
    public class SupplierInventoryService
    {
        private readonly AppDbContext _db;
        private readonly ShopifyVariantLookupService _variantLookup;
        private readonly InventorySalesCacheService _salesCacheService;
        private readonly ProductLedgerService _ledger;
        private ProductSoldAllocation? _soldAllocationCache;
        private Dictionary<int, Dictionary<string, int>>? _allSupplierSoldByLineKeyCache;
        private Dictionary<string, int>? _totalSoldByLineKeyCache;

        public SupplierInventoryService(
            AppDbContext db,
            ShopifyVariantLookupService variantLookup,
            InventorySalesCacheService salesCacheService,
            ProductLedgerService ledger )
        {
            _db = db;
            _variantLookup = variantLookup;
            _salesCacheService = salesCacheService;
            _ledger = ledger;
        }

        public async Task<SupplierInventoryResponse> GetInventoryAsync( int? supplierId, bool forceRefresh = false )
        {
            if (supplierId.HasValue && supplierId.Value <= 0)
            {
                throw new InvalidOperationException( "Некарэктны ідэнтыфікатар пастаўшчыка." );
            }

            if (supplierId.HasValue)
            {
                bool supplierExists = await _db.Suppliers.AnyAsync( s => s.Id == supplierId.Value );
                if (!supplierExists)
                {
                    throw new InvalidOperationException( "Пастаўшчык не знойдзены." );
                }
            }

            await ExpenseInvoiceTypeSeeder.EnsureDefaultAsync( _db );
            // Sync Shopify sales for months without VAT reports into InventoryProductSales cache.
            // Skips Shopify when cache is fresh; repair of broken report rows stays disabled in GetSoldByLineAsync.
            DateTime? salesSyncedAtUtc = await _salesCacheService.EnsureFreshAsync( force: forceRefresh );

            List<SupplyBatch> supplyBatches = await LoadSupplyBatchesAsync( supplierId );
            IReadOnlyDictionary<string, string> defaultVariantByProduct =
                await _ledger.GetDefaultVariantByProductAsync();
            IReadOnlyDictionary<string, string> legacySaleVariantByProduct =
                await _ledger.GetLegacySaleVariantByProductAsync();
            IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle =
                await _variantLookup.GetVariantIdByProductTitleMapCachedAsync();
            RemapSupplyBatchVariants(
                supplyBatches,
                defaultVariantByProduct,
                variantIdByTitle,
                legacySaleVariantByProduct );
            HashSet<string> productIds = supplyBatches
                .Select( x => x.ShopifyProductId )
                .Where( id => !string.IsNullOrWhiteSpace( id ) )
                .ToHashSet( StringComparer.OrdinalIgnoreCase );
            Dictionary<string, string> productNames = await BuildProductNamesAsync( productIds );
            IReadOnlyDictionary<(string ProductId, string VariantId), string> variantTitles =
                await TryLoadVariantTitlesAsync();
            IReadOnlyDictionary<(string ProductId, string VariantId), int> stockByLine =
                await TryLoadStockByLineAsync();
            Dictionary<InventoryLineKey, int> soldBySupplierLine =
                await BuildSoldBySupplierLineFromHistoryAsync(
                    supplyBatches,
                    productNames,
                    supplierId );
            Dictionary<InventoryLineKey, int> paidBySupplierLine =
                await LoadPaidBySupplierLineAsync(
                    supplierId,
                    defaultVariantByProduct,
                    variantIdByTitle,
                    legacySaleVariantByProduct );
            Dictionary<InventoryLineKey, decimal> latestSupplierPrice = supplyBatches
                .GroupBy( x => x.LineKey )
                .ToDictionary( g => g.Key, g => ResolveLatestNonZeroSupplyPrice( g ) );
            Dictionary<InventoryLineKey, decimal> latestVatRatePercent = supplyBatches
                .GroupBy( x => x.LineKey )
                .ToDictionary( g => g.Key, g => ResolveLatestSupplyVatRatePercent( g ) );
            Dictionary<InventoryLineKey, int> receivedBySupplierLine = supplyBatches
                .GroupBy( x => x.LineKey )
                .ToDictionary( g => g.Key, g => g.Sum( x => x.Quantity ) );

            HashSet<InventoryLineKey> keys = new( InventoryLineKeyComparer.Instance );
            foreach (SupplyBatch batch in supplyBatches)
            {
                keys.Add( batch.LineKey );
            }

            if (!supplierId.HasValue)
            {
                foreach (InventoryLineKey key in soldBySupplierLine.Keys)
                {
                    keys.Add( key );
                }

                foreach (InventoryLineKey key in paidBySupplierLine.Keys)
                {
                    keys.Add( key );
                }
            }

            Dictionary<int, string> supplierNames = await _db.Suppliers
                .AsNoTracking()
                .Where( s => !supplierId.HasValue || s.Id == supplierId.Value )
                .ToDictionaryAsync( s => s.Id!.Value, s => s.Name );
            Dictionary<int, bool> supplierIsVatPayer = await _db.Suppliers
                .AsNoTracking()
                .Where( s => !supplierId.HasValue || s.Id == supplierId.Value )
                .ToDictionaryAsync( s => s.Id!.Value, s => s.isVATPayer );
            Dictionary<InventoryLineKey, PriceOverrideRow> priceOverrides =
                await LoadPriceOverridesAsync( supplierId );

            List<SupplierInventoryRow> rows = keys
                .Where( key => !supplierId.HasValue || key.SupplierId == supplierId.Value )
                .Select( key =>
                {
                    productNames.TryGetValue( key.ProductId, out string? productName );
                    latestSupplierPrice.TryGetValue( key, out decimal supplyNetPrice );
                    latestVatRatePercent.TryGetValue( key, out decimal supplyVatRatePercent );
                    bool useProductTotals =
                        VariantLegacyDefaults.GetNamedVariantCount( key.ProductId, variantIdByTitle ) <= 1;
                    if (useProductTotals && supplyNetPrice <= 0m)
                    {
                        supplyNetPrice = ResolveLatestNonZeroSupplyPrice(
                            supplyBatches.Where( batch =>
                                batch.SupplierId == key.SupplierId &&
                                string.Equals(
                                    batch.ShopifyProductId,
                                    key.ProductId,
                                    StringComparison.OrdinalIgnoreCase ) ) );
                        supplyVatRatePercent = ResolveLatestSupplyVatRatePercent(
                            supplyBatches.Where( batch =>
                                batch.SupplierId == key.SupplierId &&
                                string.Equals(
                                    batch.ShopifyProductId,
                                    key.ProductId,
                                    StringComparison.OrdinalIgnoreCase ) ) );
                    }
                    int soldQuantity = useProductTotals
                        ? SumQuantityForSupplierProduct( soldBySupplierLine, key )
                        : soldBySupplierLine.GetValueOrDefault( key );
                    int paidQuantity = useProductTotals
                        ? SumQuantityForSupplierProduct( paidBySupplierLine, key )
                        : paidBySupplierLine.GetValueOrDefault( key );
                    receivedBySupplierLine.TryGetValue( key, out int receivedQuantity );
                    stockByLine.TryGetValue( (key.ProductId, key.VariantId), out int quantityInStock );
                    variantTitles.TryGetValue( (key.ProductId, key.VariantId), out string? variantTitle );
                    supplierNames.TryGetValue( key.SupplierId, out string? supplierName );
                    bool isVatPayer = supplierIsVatPayer.GetValueOrDefault( key.SupplierId );
                    (decimal netUnitPrice, decimal vatRatePercent, decimal grossUnitPrice, bool hasPriceOverride) =
                        ResolvePricing(
                            key,
                            supplyNetPrice,
                            supplyVatRatePercent,
                            isVatPayer,
                            priceOverrides );

                    int quantityToPay = soldQuantity - paidQuantity;

                    return new SupplierInventoryRow
                    {
                        SupplierId = key.SupplierId,
                        SupplierName = supplierName ?? string.Empty,
                        ShopifyProductId = key.ProductId,
                        ShopifyVariantId = key.VariantId,
                        VariantTitle = string.IsNullOrWhiteSpace( variantTitle ) ? string.Empty : variantTitle,
                        ProductName = string.IsNullOrWhiteSpace( productName ) ? key.ProductId : productName,
                        SupplierPrice = netUnitPrice,
                        VatRatePercent = vatRatePercent,
                        GrossUnitPrice = grossUnitPrice,
                        SupplierIsVatPayer = isVatPayer,
                        HasPriceOverride = hasPriceOverride,
                        ReceivedQuantity = receivedQuantity,
                        QuantityInStock = quantityInStock,
                        SoldQuantity = soldQuantity,
                        PaidQuantity = paidQuantity,
                        QuantityToPay = quantityToPay
                    };
                } )
                .OrderBy( x => x.SupplierName, StringComparer.OrdinalIgnoreCase )
                .ThenBy( x => x.ProductName, StringComparer.OrdinalIgnoreCase )
                .ThenBy( x => x.VariantTitle, StringComparer.OrdinalIgnoreCase )
                .ToList();

            return new SupplierInventoryResponse
            {
                Rows = rows,
                SalesSyncedAtUtc = salesSyncedAtUtc
            };
        }

        /// <summary>
        /// Sold qty per product line key for one supplier (ledger + supply FIFO).
        /// Used by overpayment logic so it matches inventory sold/paid columns.
        /// </summary>
        public async Task<Dictionary<string, int>> GetSoldQuantityBySupplierLineKeyAsync( int supplierId )
        {
            if (supplierId <= 0)
            {
                throw new InvalidOperationException( "Некарэктны ідэнтыфікатар пастаўшчыка." );
            }

            Dictionary<int, Dictionary<string, int>> allSupplierSold =
                await GetAllSoldQuantityBySupplierLineKeyAsync();
            return allSupplierSold.GetValueOrDefault( supplierId ) ?? new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
        }

        public async Task<Dictionary<int, Dictionary<string, int>>> GetAllSoldQuantityBySupplierLineKeyAsync()
        {
            if (_allSupplierSoldByLineKeyCache is not null)
            {
                return _allSupplierSoldByLineKeyCache;
            }

            IReadOnlyDictionary<string, string> defaultVariantByProduct =
                await _ledger.GetDefaultVariantByProductAsync();
            IReadOnlyDictionary<string, string> legacySaleVariantByProduct =
                await _ledger.GetLegacySaleVariantByProductAsync();
            IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle =
                await _variantLookup.GetVariantIdByProductTitleMapCachedAsync();
            List<SupplyBatch> supplyBatches = await LoadSupplyBatchesAsync( supplierId: null );
            RemapSupplyBatchVariants(
                supplyBatches,
                defaultVariantByProduct,
                variantIdByTitle,
                legacySaleVariantByProduct );
            HashSet<string> productIds = supplyBatches
                .Select( batch => batch.ShopifyProductId )
                .Where( id => !string.IsNullOrWhiteSpace( id ) )
                .ToHashSet( StringComparer.OrdinalIgnoreCase );
            Dictionary<string, string> productNames = await BuildProductNamesAsync( productIds );

            Dictionary<int, Dictionary<string, int>> result = new();
            foreach (IGrouping<int, SupplyBatch> supplierGroup in supplyBatches.GroupBy( batch => batch.LineKey.SupplierId ))
            {
                Dictionary<string, string> supplierProductNames = supplierGroup
                    .Select( batch => batch.ShopifyProductId )
                    .Distinct( StringComparer.OrdinalIgnoreCase )
                    .ToDictionary(
                        id => id,
                        id =>
                        {
                            productNames.TryGetValue( id, out string? name );
                            return name ?? string.Empty;
                        },
                        StringComparer.OrdinalIgnoreCase );
                Dictionary<InventoryLineKey, int> soldBySupplierLine = await BuildSoldBySupplierLineFromHistoryAsync(
                    supplierGroup.ToList(),
                    supplierProductNames,
                    supplierGroup.Key );
                result[supplierGroup.Key] = AggregateSoldByLineKey(
                    soldBySupplierLine,
                    defaultVariantByProduct,
                    variantIdByTitle,
                    legacySaleVariantByProduct );
            }

            _allSupplierSoldByLineKeyCache = result;
            return result;
        }

        /// <summary>
        /// Total sold qty per product line key across all suppliers (ledger, with refund adjustments).
        /// </summary>
        public async Task<Dictionary<string, int>> GetTotalSoldQuantityByLineKeyAsync()
        {
            if (_totalSoldByLineKeyCache is not null)
            {
                return _totalSoldByLineKeyCache;
            }

            Dictionary<int, Dictionary<string, int>> soldBySupplier =
                await GetAllSoldQuantityBySupplierLineKeyAsync();
            Dictionary<string, int> sold = new( StringComparer.OrdinalIgnoreCase );
            foreach (Dictionary<string, int> supplierSold in soldBySupplier.Values)
            {
                foreach (KeyValuePair<string, int> entry in supplierSold)
                {
                    sold[entry.Key] = sold.GetValueOrDefault( entry.Key ) + entry.Value;
                }
            }

            _totalSoldByLineKeyCache = sold;
            return sold;
        }

        private async Task<ProductSoldAllocation> GetSoldAllocationCachedAsync() =>
            _soldAllocationCache ??= await _ledger.GetSoldByLineAsync();

        private static Dictionary<string, int> AggregateSoldByLineKey(
            Dictionary<InventoryLineKey, int> soldBySupplierLine,
            IReadOnlyDictionary<string, string> defaultVariantByProduct,
            IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle,
            IReadOnlyDictionary<string, string> legacySaleVariantByProduct )
        {
            Dictionary<string, int> sold = new( StringComparer.OrdinalIgnoreCase );
            foreach (KeyValuePair<InventoryLineKey, int> entry in soldBySupplierLine )
            {
                if (entry.Value <= 0)
                {
                    continue;
                }

                InventoryLineKey key = entry.Key;
                string lineKey = ProductLedgerService.BuildStrictProductLineKey(
                    key.ProductId,
                    key.VariantId,
                    defaultVariantByProduct,
                    variantIdByTitle,
                    legacySaleVariantByProduct );
                if (string.IsNullOrWhiteSpace( lineKey ))
                {
                    continue;
                }

                sold[lineKey] = sold.GetValueOrDefault( lineKey ) + entry.Value;
            }

            return sold;
        }

        public async Task<SupplierInventoryRow> UpdatePricingAsync( SupplierInventoryPricingUpdateRequest request )
        {
            if (request.SupplierId <= 0)
            {
                throw new InvalidOperationException( "Некарэктны ідэнтыфікатар пастаўшчыка." );
            }

            string productId = NormalizeProductId( request.ShopifyProductId );
            if (string.IsNullOrWhiteSpace( productId ))
            {
                throw new InvalidOperationException( "Некарэктны ідэнтыфікатар тавару." );
            }

            string variantId = NormalizeVariantId( request.ShopifyVariantId );
            if (request.NetUnitPrice < 0m)
            {
                throw new InvalidOperationException( "Кошт нета не можа быць адмоўным." );
            }

            Supplier? supplier = await _db.Suppliers
                .AsNoTracking()
                .FirstOrDefaultAsync( s => s.Id == request.SupplierId );
            if (supplier is null)
            {
                throw new InvalidOperationException( "Пастаўшчык не знойдзены." );
            }

            decimal vatRatePercent = supplier.isVATPayer
                ? Math.Clamp( request.VatRatePercent, 0m, 100m )
                : 0m;
            decimal netUnitPrice = Round2( request.NetUnitPrice );
            decimal grossUnitPrice = CalcGrossUnitPrice( netUnitPrice, vatRatePercent, supplier.isVATPayer );

            SupplierInventoryPriceOverride? existing = await _db.SupplierInventoryPriceOverrides
                .FirstOrDefaultAsync( row =>
                    row.SupplierId == request.SupplierId &&
                    row.ShopifyProductId == productId &&
                    row.ShopifyVariantId == variantId );
            if (existing is null)
            {
                existing = new SupplierInventoryPriceOverride
                {
                    SupplierId = request.SupplierId,
                    ShopifyProductId = productId,
                    ShopifyVariantId = variantId
                };
                _db.SupplierInventoryPriceOverrides.Add( existing );
            }

            existing.NetUnitPrice = netUnitPrice;
            existing.VatRatePercent = vatRatePercent;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();


            IReadOnlyDictionary<(string ProductId, string VariantId), string> variantTitles =
                await TryLoadVariantTitlesAsync();
            Dictionary<string, string> productNames = await BuildProductNamesAsync( new HashSet<string>( [productId], StringComparer.OrdinalIgnoreCase ) );
            productNames.TryGetValue( productId, out string? productName );
            variantTitles.TryGetValue( (productId, variantId), out string? variantTitle );

            return new SupplierInventoryRow
            {
                SupplierId = request.SupplierId,
                SupplierName = supplier.Name ?? string.Empty,
                ShopifyProductId = productId,
                ShopifyVariantId = variantId,
                VariantTitle = variantTitle ?? string.Empty,
                ProductName = string.IsNullOrWhiteSpace( productName ) ? productId : productName,
                SupplierPrice = netUnitPrice,
                VatRatePercent = vatRatePercent,
                GrossUnitPrice = grossUnitPrice,
                SupplierIsVatPayer = supplier.isVATPayer,
                HasPriceOverride = true
            };
        }

        private async Task<Dictionary<InventoryLineKey, PriceOverrideRow>> LoadPriceOverridesAsync( int? supplierId )
        {
            List<PriceOverrideRow> rows = await _db.SupplierInventoryPriceOverrides
                .AsNoTracking()
                .Where( row => !supplierId.HasValue || row.SupplierId == supplierId.Value )
                .Select( row => new PriceOverrideRow
                {
                    SupplierId = row.SupplierId,
                    ShopifyProductId = row.ShopifyProductId,
                    ShopifyVariantId = row.ShopifyVariantId,
                    NetUnitPrice = row.NetUnitPrice,
                    VatRatePercent = row.VatRatePercent
                } )
                .ToListAsync();

            return rows.ToDictionary(
                row => new InventoryLineKey(
                    row.SupplierId,
                    NormalizeProductId( row.ShopifyProductId ),
                    NormalizeVariantId( row.ShopifyVariantId ) ),
                row => row,
                InventoryLineKeyComparer.Instance );
        }

        private static (decimal NetUnitPrice, decimal VatRatePercent, decimal GrossUnitPrice, bool HasPriceOverride)
            ResolvePricing(
                InventoryLineKey key,
                decimal supplyNetPrice,
                decimal supplyVatRatePercent,
                bool supplierIsVatPayer,
                IReadOnlyDictionary<InventoryLineKey, PriceOverrideRow> priceOverrides )
        {
            decimal netUnitPrice = supplyNetPrice;
            decimal vatRatePercent = supplierIsVatPayer ? supplyVatRatePercent : 0m;
            bool hasPriceOverride = false;

            if (priceOverrides.TryGetValue( key, out PriceOverrideRow? priceOverride ))
            {
                netUnitPrice = priceOverride.NetUnitPrice;
                vatRatePercent = supplierIsVatPayer ? priceOverride.VatRatePercent : 0m;
                hasPriceOverride = true;
            }

            if (!supplierIsVatPayer)
            {
                vatRatePercent = 0m;
            }

            decimal grossUnitPrice = CalcGrossUnitPrice( netUnitPrice, vatRatePercent, supplierIsVatPayer );
            return (netUnitPrice, vatRatePercent, grossUnitPrice, hasPriceOverride);
        }

        private static decimal ResolveLatestNonZeroSupplyPrice( IEnumerable<SupplyBatch> batches )
        {
            SupplyBatch? latestWithPrice = batches
                .Where( batch => batch.SupplierPrice > 0m )
                .OrderByDescending( batch => batch.SupplyDate )
                .ThenByDescending( batch => batch.SupplyId )
                .FirstOrDefault();
            if (latestWithPrice is not null)
            {
                return latestWithPrice.SupplierPrice;
            }

            return batches
                .OrderByDescending( batch => batch.SupplyDate )
                .ThenByDescending( batch => batch.SupplyId )
                .Select( batch => batch.SupplierPrice )
                .FirstOrDefault();
        }

        private static decimal ResolveLatestSupplyVatRatePercent( IEnumerable<SupplyBatch> batches )
        {
            SupplyBatch? latestWithPrice = batches
                .Where( batch => batch.SupplierPrice > 0m )
                .OrderByDescending( batch => batch.SupplyDate )
                .ThenByDescending( batch => batch.SupplyId )
                .FirstOrDefault();
            if (latestWithPrice is not null)
            {
                return latestWithPrice.VatRatePercent;
            }

            return batches
                .OrderByDescending( batch => batch.SupplyDate )
                .ThenByDescending( batch => batch.SupplyId )
                .Select( batch => batch.VatRatePercent )
                .FirstOrDefault();
        }

        private static decimal CalcGrossUnitPrice( decimal netUnitPrice, decimal vatRatePercent, bool supplierIsVatPayer )
        {
            if (!supplierIsVatPayer)
            {
                return Round2( netUnitPrice );
            }

            return Round2( netUnitPrice * (1m + vatRatePercent / 100m ) );
        }

        private static decimal Round2( decimal value ) => Math.Round( value, 2, MidpointRounding.AwayFromZero );

        private async Task<List<SupplyBatch>> LoadSupplyBatchesAsync( int? supplierId )
        {
            List<SupplyProduct> supplyProducts = await _db.SupplyProducts
                .AsNoTracking()
                .Include( sp => sp.Supply )
                .Where( sp => !supplierId.HasValue || sp.Supply.SupplierId == supplierId.Value )
                .OrderBy( sp => sp.Supply.Date )
                .ThenBy( sp => sp.Supply.Id )
                .ThenBy( sp => sp.Id )
                .ToListAsync();

            return supplyProducts
                .Select( sp =>
                {
                    string productId = NormalizeProductId( sp.ShopifyProductId );
                    string variantId = NormalizeVariantId( sp.ShopifyVariantId );
                    return new SupplyBatch
                    {
                        SupplyId = sp.SupplyId,
                        SupplyDate = sp.Supply.Date,
                        SupplierId = sp.Supply.SupplierId,
                        ShopifyProductId = productId,
                        ShopifyVariantId = variantId,
                        LineKey = new InventoryLineKey( sp.Supply.SupplierId, productId, variantId ),
                        Quantity = sp.Quantity,
                        SupplierPrice = sp.SupplierPrice,
                        VatRatePercent = sp.VatRatePercent
                    };
                } )
                .ToList();
        }

        private async Task<IReadOnlyDictionary<(string ProductId, string VariantId), string>> TryLoadVariantTitlesAsync()
        {
            try
            {
                return await _variantLookup.GetVariantTitleByLineMapCachedAsync();
            }
            catch
            {
                return new Dictionary<(string ProductId, string VariantId), string>( ProductVariantKeyComparer.Instance );
            }
        }

        private async Task<IReadOnlyDictionary<(string ProductId, string VariantId), int>> TryLoadStockByLineAsync()
        {
            try
            {
                return await _variantLookup.GetStockByLineMapCachedAsync();
            }
            catch
            {
                return new Dictionary<(string ProductId, string VariantId), int>( ProductVariantKeyComparer.Instance );
            }
        }

        private async Task<Dictionary<string, string>> BuildProductNamesAsync( HashSet<string> productIds )
        {
            Dictionary<string, string> names = new( StringComparer.OrdinalIgnoreCase );
            if (productIds.Count == 0)
            {
                return names;
            }

            try
            {
                IReadOnlyDictionary<string, string> catalogNames =
                    await _variantLookup.GetProductTitleByIdMapCachedAsync();
                foreach (string productId in productIds)
                {
                    if (catalogNames.TryGetValue( productId, out string? catalogName ) &&
                        !string.IsNullOrWhiteSpace( catalogName ))
                    {
                        names[productId] = catalogName.Trim();
                    }
                }
            }
            catch
            {
                // Shopify catalog is optional for names; fall back to expense titles below.
            }

            List<string> productIdList = productIds.ToList();
            var expenseProducts = await _db.VatReportExpenseProducts
                .AsNoTracking()
                .Where( p => productIdList.Contains( p.ShopifyProductId ) )
                .Select( p => new { p.ShopifyProductId, p.ProductTitle } )
                .ToListAsync();
            foreach (var product in expenseProducts)
            {
                string productId = NormalizeProductId( product.ShopifyProductId );
                if (string.IsNullOrWhiteSpace( productId )) continue;
                if (!string.IsNullOrWhiteSpace( product.ProductTitle ))
                {
                    names[productId] = product.ProductTitle.Trim();
                }
            }

            return names;
        }

        private async Task<Dictionary<InventoryLineKey, int>> BuildSoldBySupplierLineFromHistoryAsync(
            List<SupplyBatch> supplyBatches,
            Dictionary<string, string> productNames,
            int? supplierId )
        {
            Dictionary<InventoryLineKey, int> soldBySupplierLine = new( InventoryLineKeyComparer.Instance );
            foreach (IGrouping<string, SupplyBatch> productGroup in supplyBatches.GroupBy(
                batch => batch.ShopifyProductId,
                StringComparer.OrdinalIgnoreCase ))
            {
                string productId = productGroup.Key;
                if (string.IsNullOrWhiteSpace( productId ))
                {
                    continue;
                }

                productNames.TryGetValue( productId, out string? productName );
                List<ProductHistorySaleEvent> sales = await _ledger.GetSaleEventsForProductAsync(
                    productId,
                    normalizedVariantFilter: null,
                    filterVariantTitle: null,
                    supplierId,
                    productName );

                IEnumerable<IGrouping<string, ProductHistorySaleEvent>> salesByVariant = sales
                    .GroupBy( sale => sale.ShopifyVariantId ?? string.Empty, StringComparer.OrdinalIgnoreCase );
                foreach (IGrouping<string, ProductHistorySaleEvent> variantGroup in salesByVariant)
                {
                    string variantId = variantGroup.Key;
                    int totalSold = variantGroup.Sum( sale => sale.Quantity );
                    if (totalSold <= 0)
                    {
                        continue;
                    }

                    List<SupplyBatch> variantBatches = productGroup
                        .Where( batch =>
                            string.IsNullOrWhiteSpace( variantId ) ||
                            string.Equals(
                                batch.ShopifyVariantId,
                                variantId,
                                StringComparison.OrdinalIgnoreCase ) )
                        .OrderBy( batch => batch.SupplyDate )
                        .ThenBy( batch => batch.SupplyId )
                        .ToList();
                    if (variantBatches.Count == 0)
                    {
                        variantBatches = productGroup
                            .OrderBy( batch => batch.SupplyDate )
                            .ThenBy( batch => batch.SupplyId )
                            .ToList();
                    }

                    int remaining = totalSold;
                    foreach (SupplyBatch batch in variantBatches)
                    {
                        if (remaining <= 0)
                        {
                            break;
                        }

                        int allocated = Math.Min( remaining, Math.Max( 0, batch.Quantity ) );
                        if (allocated <= 0)
                        {
                            continue;
                        }

                        soldBySupplierLine[batch.LineKey] =
                            soldBySupplierLine.GetValueOrDefault( batch.LineKey ) + allocated;
                        remaining -= allocated;
                    }
                }
            }

            return soldBySupplierLine;
        }

        private static int SumQuantityForSupplierProduct(
            Dictionary<InventoryLineKey, int> quantities,
            InventoryLineKey rowKey )
        {
            int total = 0;
            foreach (KeyValuePair<InventoryLineKey, int> entry in quantities)
            {
                if (entry.Key.SupplierId != rowKey.SupplierId ||
                    !string.Equals( entry.Key.ProductId, rowKey.ProductId, StringComparison.OrdinalIgnoreCase ))
                {
                    continue;
                }

                total += entry.Value;
            }

            return total;
        }

        private static Dictionary<InventoryLineKey, int> AllocateSoldBySupplierFifo(
            Dictionary<(string ProductId, string VariantId), int> soldByLine,
            Dictionary<string, int> legacyUnnamedSoldByProduct,
            List<SupplyBatch> supplyBatches )
        {
            Dictionary<InventoryLineKey, int> soldBySupplierLine = new( InventoryLineKeyComparer.Instance );
            Dictionary<(string ProductId, string VariantId), int> remainingSoldByLine =
                new( soldByLine, ProductVariantKeyComparer.Instance );

            IEnumerable<(string ProductId, string VariantId)> lineIds = supplyBatches
                .Select( x => (x.ShopifyProductId, x.ShopifyVariantId) )
                .Concat( remainingSoldByLine.Keys )
                .Distinct( ProductVariantKeyComparer.Instance );

            foreach ((string productId, string variantId) in lineIds)
            {
                if (string.IsNullOrWhiteSpace( variantId ))
                {
                    continue;
                }

                (string ProductId, string VariantId) lineKey = (productId, variantId);
                int remainingSold = remainingSoldByLine.GetValueOrDefault( lineKey );
                if (remainingSold <= 0) continue;

                remainingSold = AllocateToMatchingBatches(
                    supplyBatches.Where( batch =>
                        ProductVariantEquals( batch.ShopifyProductId, batch.ShopifyVariantId, productId, variantId ) ),
                    remainingSold,
                    soldBySupplierLine );
                remainingSoldByLine[lineKey] = remainingSold;
            }

            foreach ((string ProductId, string VariantId) lineKey in remainingSoldByLine.Keys.ToList())
            {
                int remainingSold = remainingSoldByLine[lineKey];
                if (remainingSold <= 0)
                {
                    continue;
                }

                remainingSold = AllocateToMatchingBatches(
                    supplyBatches
                        .Where( batch => string.Equals(
                            batch.ShopifyProductId,
                            lineKey.ProductId,
                            StringComparison.OrdinalIgnoreCase ) )
                        .OrderBy( batch => batch.SupplyDate )
                        .ThenBy( batch => batch.SupplyId ),
                    remainingSold,
                    soldBySupplierLine );
                remainingSoldByLine[lineKey] = remainingSold;
            }

            foreach ((string ProductId, string VariantId) lineKey in remainingSoldByLine.Keys.ToList())
            {
                int remainingSold = remainingSoldByLine[lineKey];
                if (remainingSold <= 0)
                {
                    continue;
                }

                legacyUnnamedSoldByProduct[lineKey.ProductId] =
                    legacyUnnamedSoldByProduct.GetValueOrDefault( lineKey.ProductId ) + remainingSold;
                remainingSoldByLine[lineKey] = 0;
            }

            foreach (string productId in legacyUnnamedSoldByProduct.Keys.ToList())
            {
                int remainingSold = legacyUnnamedSoldByProduct.GetValueOrDefault( productId );
                if (remainingSold <= 0) continue;

                remainingSold = AllocateToMatchingBatches(
                    supplyBatches
                        .Where( batch => string.Equals(
                            batch.ShopifyProductId,
                            productId,
                            StringComparison.OrdinalIgnoreCase ) )
                        .OrderBy( batch => batch.SupplyDate )
                        .ThenBy( batch => batch.SupplyId ),
                    remainingSold,
                    soldBySupplierLine );
                legacyUnnamedSoldByProduct[productId] = remainingSold;
            }

            return soldBySupplierLine;
        }

        private static int AllocateToMatchingBatches(
            IEnumerable<SupplyBatch> batches,
            int remainingSold,
            Dictionary<InventoryLineKey, int> soldBySupplierLine )
        {
            foreach (SupplyBatch batch in batches)
            {
                if (remainingSold <= 0) break;
                int allocated = Math.Min( remainingSold, Math.Max( 0, batch.Quantity ) );
                if (allocated <= 0) continue;

                soldBySupplierLine[batch.LineKey] = soldBySupplierLine.GetValueOrDefault( batch.LineKey ) + allocated;
                remainingSold -= allocated;
            }

            return remainingSold;
        }

        private static bool ProductVariantEquals(
            string leftProductId,
            string leftVariantId,
            string rightProductId,
            string rightVariantId ) =>
            string.Equals( leftProductId, rightProductId, StringComparison.OrdinalIgnoreCase ) &&
            string.Equals( leftVariantId, rightVariantId, StringComparison.OrdinalIgnoreCase );

        private static void RemapSupplyBatchVariants(
            List<SupplyBatch> supplyBatches,
            IReadOnlyDictionary<string, string> defaultVariantByProduct,
            IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle,
            IReadOnlyDictionary<string, string> legacySaleVariantByProduct )
        {
            foreach (SupplyBatch batch in supplyBatches)
            {
                batch.ShopifyVariantId = VariantLegacyDefaults.ResolveVariantId(
                    batch.ShopifyProductId,
                    batch.ShopifyVariantId,
                    defaultVariantByProduct,
                    variantIdByTitle,
                    legacySaleVariantByProduct );
                batch.LineKey = new InventoryLineKey(
                    batch.SupplierId,
                    batch.ShopifyProductId,
                    batch.ShopifyVariantId );
            }
        }

        private async Task<Dictionary<InventoryLineKey, int>> LoadPaidBySupplierLineAsync(
            int? supplierId,
            IReadOnlyDictionary<string, string> defaultVariantByProduct,
            IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle,
            IReadOnlyDictionary<string, string> legacySaleVariantByProduct )
        {
            List<PaidProductRow> paidProducts = await _db.VatReportExpenseProducts
                .AsNoTracking()
                .Where( p =>
                    p.VatReportExpense.SupplierId.HasValue &&
                    (!supplierId.HasValue || p.VatReportExpense.SupplierId.Value == supplierId.Value) &&
                    p.VatReportExpense.ExpenseInvoiceType.Name == ExpenseInvoiceTypeSeeder.SupplierPaymentDefaultName )
                .Select( p => new PaidProductRow
                {
                    SupplierId = p.VatReportExpense.SupplierId!.Value,
                    ShopifyProductId = p.ShopifyProductId,
                    ShopifyVariantId = p.ShopifyVariantId,
                    Quantity = p.Quantity
                } )
                .ToListAsync();

            return paidProducts
                .GroupBy( p => new InventoryLineKey(
                    p.SupplierId,
                    NormalizeProductId( p.ShopifyProductId ),
                    ProductLedgerService.ResolvePaymentVariantId(
                        p.ShopifyProductId,
                        NormalizeVariantId( p.ShopifyVariantId ),
                        defaultVariantByProduct,
                        variantIdByTitle,
                        legacySaleVariantByProduct ) ) )
                .ToDictionary( g => g.Key, g => g.Sum( x => x.Quantity ), InventoryLineKeyComparer.Instance );
        }

        private static string NormalizeProductId( string raw )
        {
            if (string.IsNullOrWhiteSpace( raw )) return string.Empty;
            return ShopifyIds.NormalizeProductId( raw.Trim() );
        }

        private static string NormalizeVariantId( string raw )
        {
            if (string.IsNullOrWhiteSpace( raw )) return string.Empty;
            return ShopifyIds.NormalizeVariantId( raw.Trim() );
        }

        private readonly record struct InventoryLineKey( int SupplierId, string ProductId, string VariantId );

        private sealed class InventoryLineKeyComparer : IEqualityComparer<InventoryLineKey>
        {
            public static InventoryLineKeyComparer Instance { get; } = new();

            public bool Equals( InventoryLineKey x, InventoryLineKey y ) =>
                x.SupplierId == y.SupplierId &&
                string.Equals( x.ProductId, y.ProductId, StringComparison.OrdinalIgnoreCase ) &&
                string.Equals( x.VariantId, y.VariantId, StringComparison.OrdinalIgnoreCase );

            public int GetHashCode( InventoryLineKey obj ) =>
                HashCode.Combine(
                    obj.SupplierId,
                    StringComparer.OrdinalIgnoreCase.GetHashCode( obj.ProductId ),
                    StringComparer.OrdinalIgnoreCase.GetHashCode( obj.VariantId ) );
        }

        private sealed class SupplyBatch
        {
            public int SupplyId { get; set; }
            public DateOnly SupplyDate { get; set; }
            public int SupplierId { get; set; }
            public string ShopifyProductId { get; set; } = string.Empty;
            public string ShopifyVariantId { get; set; } = string.Empty;
            public InventoryLineKey LineKey { get; set; }
            public int Quantity { get; set; }
            public decimal SupplierPrice { get; set; }
            public decimal VatRatePercent { get; set; }
        }

        private sealed class PaidProductRow
        {
            public int SupplierId { get; set; }
            public string ShopifyProductId { get; set; } = string.Empty;
            public string ShopifyVariantId { get; set; } = string.Empty;
            public int Quantity { get; set; }
        }

        private sealed class PriceOverrideRow
        {
            public int SupplierId { get; set; }
            public string ShopifyProductId { get; set; } = string.Empty;
            public string ShopifyVariantId { get; set; } = string.Empty;
            public decimal NetUnitPrice { get; set; }
            public decimal VatRatePercent { get; set; }
        }
    }
}
