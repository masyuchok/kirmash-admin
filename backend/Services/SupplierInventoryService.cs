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
        private readonly ShopifyInventoryService _shopifyInventory;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private Dictionary<int, Dictionary<string, int>>? _allSupplierSoldByLineKeyCache;
        private Dictionary<string, int>? _totalSoldByLineKeyCache;

        public SupplierInventoryService(
            AppDbContext db,
            ShopifyVariantLookupService variantLookup,
            InventorySalesCacheService salesCacheService,
            ProductLedgerService ledger,
            ShopifyInventoryService shopifyInventory,
            IHttpContextAccessor httpContextAccessor )
        {
            _db = db;
            _variantLookup = variantLookup;
            _salesCacheService = salesCacheService;
            _ledger = ledger;
            _shopifyInventory = shopifyInventory;
            _httpContextAccessor = httpContextAccessor;
        }

        public void InvalidateSoldAllocationCaches()
        {
            _allSupplierSoldByLineKeyCache = null;
            _totalSoldByLineKeyCache = null;
            ProductLedgerService.InvalidateSoldByLineCache();
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
            // Shopify sync is slow (live API per unreported month); only block on explicit refresh.
            DateTime? salesSyncedAtUtc = forceRefresh
                ? await _salesCacheService.EnsureFreshAsync( force: true )
                : await _salesCacheService.GetLastSyncedAtUtcAsync();
            if (forceRefresh)
            {
                InvalidateSoldAllocationCaches();
            }

            List<SupplyBatch> supplyBatches = await LoadSupplyBatchesAsync( supplierId );
            List<SupplyBatch> fifoSupplyBatches = supplierId.HasValue
                ? await LoadSupplyBatchesAsync( supplierId: null )
                : supplyBatches;
            IReadOnlyDictionary<string, string> defaultVariantByProduct =
                await _ledger.GetDefaultVariantByProductAsync();
            IReadOnlyDictionary<string, string> legacySaleVariantByProduct =
                await _ledger.GetLegacySaleVariantByProductAsync();
            IReadOnlyDictionary<string, Dictionary<string, string>> variantIdByTitle =
                await TryLoadVariantIdByTitleAsync();
            RemapSupplyBatchVariants(
                supplyBatches,
                defaultVariantByProduct,
                variantIdByTitle,
                legacySaleVariantByProduct );
            RemapSupplyBatchVariants(
                fifoSupplyBatches,
                defaultVariantByProduct,
                variantIdByTitle,
                legacySaleVariantByProduct );
            HashSet<string> productIds = supplyBatches
                .Select( x => x.ShopifyProductId )
                .Where( id => !string.IsNullOrWhiteSpace( id ) )
                .ToHashSet( StringComparer.OrdinalIgnoreCase );
            Dictionary<string, string> productNames = await BuildProductNamesAsync( productIds );
            Dictionary<string, string> productAuthors = await TryLoadProductAuthorsAsync( productIds );
            Dictionary<string, string> productTypes = await TryLoadProductTypesAsync( productIds );
            IReadOnlyDictionary<(string ProductId, string VariantId), string> variantTitles =
                await TryLoadVariantTitlesAsync();
            IReadOnlyDictionary<(string ProductId, string VariantId), int> stockByLine =
                await TryLoadStockByLineAsync();
            Dictionary<InventoryLineKey, int> soldBySupplierLine =
                await BuildSoldBySupplierLineFastAsync( fifoSupplyBatches );
            Dictionary<InventoryLineKey, int> paidBySupplierLine =
                await LoadPaidBySupplierLineAsync(
                    supplierId,
                    defaultVariantByProduct,
                    variantIdByTitle,
                    legacySaleVariantByProduct );
            Dictionary<InventoryLineKey, int> allPaidBySupplierLine = supplierId.HasValue
                ? await LoadPaidBySupplierLineAsync(
                    supplierId: null,
                    defaultVariantByProduct,
                    variantIdByTitle,
                    legacySaleVariantByProduct )
                : paidBySupplierLine;
            Dictionary<InventoryLineKey, decimal> latestSupplierPrice = supplyBatches
                .GroupBy( x => x.LineKey )
                .ToDictionary( g => g.Key, g => ResolveLatestNonZeroSupplyPrice( g ) );
            Dictionary<InventoryLineKey, decimal> latestVatRatePercent = supplyBatches
                .GroupBy( x => x.LineKey )
                .ToDictionary( g => g.Key, g => ResolveLatestSupplyVatRatePercent( g ) );
            Dictionary<InventoryLineKey, decimal> latestMarginPercent = supplyBatches
                .GroupBy( x => x.LineKey )
                .ToDictionary( g => g.Key, g => ResolveLatestMarginPercent( g ) );
            Dictionary<InventoryLineKey, decimal> latestSalePrice = supplyBatches
                .GroupBy( x => x.LineKey )
                .ToDictionary( g => g.Key, g => ResolveLatestSalePrice( g ) );
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

            Dictionary<string, decimal> shopifyPrices = await TryLoadShopifyPricesAsync( keys );

            List<SupplierInventoryRow> rows = keys
                .Where( key => !supplierId.HasValue || key.SupplierId == supplierId.Value )
                .Select( key =>
                {
                    productNames.TryGetValue( key.ProductId, out string? productName );
                    productAuthors.TryGetValue( key.ProductId, out string? productAuthor );
                    productTypes.TryGetValue( key.ProductId, out string? productType );
                    latestSupplierPrice.TryGetValue( key, out decimal supplyUnitPrice );
                    latestVatRatePercent.TryGetValue( key, out decimal supplyVatRatePercent );
                    latestMarginPercent.TryGetValue( key, out decimal supplyMarginPercent );
                    latestSalePrice.TryGetValue( key, out decimal supplySalePrice );
                    bool useProductTotals =
                        VariantLegacyDefaults.GetNamedVariantCount( key.ProductId, variantIdByTitle ) <= 1;
                    if (useProductTotals && supplyUnitPrice <= 0m)
                    {
                        supplyUnitPrice = ResolveLatestNonZeroSupplyPrice(
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
                        supplyMarginPercent = ResolveLatestMarginPercent(
                            supplyBatches.Where( batch =>
                                batch.SupplierId == key.SupplierId &&
                                string.Equals(
                                    batch.ShopifyProductId,
                                    key.ProductId,
                                    StringComparison.OrdinalIgnoreCase ) ) );
                        supplySalePrice = ResolveLatestSalePrice(
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
                    int quantityInStock = ResolveQuantityInStock(
                        key.ProductId,
                        key.VariantId,
                        useProductTotals,
                        stockByLine,
                        defaultVariantByProduct );
                    variantTitles.TryGetValue( (key.ProductId, key.VariantId), out string? variantTitle );
                    supplierNames.TryGetValue( key.SupplierId, out string? supplierName );
                    bool isVatPayer = supplierIsVatPayer.GetValueOrDefault( key.SupplierId );
                    (decimal netUnitPrice, decimal vatRatePercent, decimal grossUnitPrice, decimal marginPercent, decimal salePrice, bool hasPriceOverride) =
                        ResolvePricing(
                            key,
                            supplyUnitPrice,
                            supplyVatRatePercent,
                            supplyMarginPercent,
                            supplySalePrice,
                            isVatPayer,
                            priceOverrides );
                    shopifyPrices.TryGetValue(
                        BuildShopifyPriceKey( key.ProductId, key.VariantId ),
                        out decimal shopifyPrice );

                    int quantityToPay = ComputeQuantityToPay(
                        key,
                        soldQuantity,
                        paidQuantity,
                        soldBySupplierLine,
                        allPaidBySupplierLine,
                        useProductTotals );

                    return new SupplierInventoryRow
                    {
                        SupplierId = key.SupplierId,
                        SupplierName = supplierName ?? string.Empty,
                        ShopifyProductId = key.ProductId,
                        ShopifyVariantId = key.VariantId,
                        VariantTitle = VariantLegacyDefaults.IsDefaultVariantTitle( variantTitle )
                            ? string.Empty
                            : variantTitle!.Trim(),
                        ProductName = string.IsNullOrWhiteSpace( productName ) ? key.ProductId : productName,
                        ProductAuthor = productAuthor ?? string.Empty,
                        ProductType = productType ?? string.Empty,
                        SupplierPrice = netUnitPrice,
                        VatRatePercent = vatRatePercent,
                        GrossUnitPrice = grossUnitPrice,
                        SupplierIsVatPayer = isVatPayer,
                        HasPriceOverride = hasPriceOverride,
                        MarginPercent = marginPercent,
                        SalePrice = salePrice,
                        ShopifyPrice = shopifyPrice,
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
                await TryLoadVariantIdByTitleAsync();
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
            Dictionary<InventoryLineKey, int> soldBySupplierLine =
                await BuildSoldBySupplierLineFastAsync( supplyBatches );
            foreach (IGrouping<int, SupplyBatch> supplierGroup in supplyBatches.GroupBy( batch => batch.LineKey.SupplierId ))
            {
                Dictionary<InventoryLineKey, int> supplierSold = soldBySupplierLine
                    .Where( entry => entry.Key.SupplierId == supplierGroup.Key )
                    .ToDictionary( entry => entry.Key, entry => entry.Value, InventoryLineKeyComparer.Instance );
                result[supplierGroup.Key] = AggregateSoldByLineKey(
                    supplierSold,
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

        public async Task<Dictionary<string, (decimal GrossUnitPrice, decimal VatRatePercent)>> GetExpenseCatalogPricingAsync( int supplierId )
        {
            if (supplierId <= 0)
            {
                return new Dictionary<string, (decimal GrossUnitPrice, decimal VatRatePercent)>( StringComparer.OrdinalIgnoreCase );
            }

            SupplierInventoryResponse inventory = await GetInventoryAsync( supplierId );
            Dictionary<string, (decimal GrossUnitPrice, decimal VatRatePercent)> result =
                new( StringComparer.OrdinalIgnoreCase );
            foreach (SupplierInventoryRow row in inventory.Rows)
            {
                string lineKey = string.IsNullOrWhiteSpace( row.ShopifyVariantId )
                    ? row.ShopifyProductId
                    : $"{row.ShopifyProductId}::{row.ShopifyVariantId}";
                result[lineKey] = (row.GrossUnitPrice, row.VatRatePercent);
            }

            return result;
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
            if (request.NetUnitPrice < 0m || request.SalePrice < 0m || request.MarginPercent < 0m)
            {
                throw new InvalidOperationException( "Цены не могуць быць адмоўнымі." );
            }

            Supplier? supplier = await _db.Suppliers
                .AsNoTracking()
                .FirstOrDefaultAsync( s => s.Id == request.SupplierId );
            if (supplier is null)
            {
                throw new InvalidOperationException( "Пастаўшчык не знойдзены." );
            }

            decimal vatRatePercent = NormalizeCatalogVatRate( request.VatRatePercent );
            decimal netUnitPrice = Round2( request.NetUnitPrice );
            decimal marginPercent = Round2( request.MarginPercent );
            decimal salePrice = Round2( request.SalePrice );
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
            existing.MarginPercent = marginPercent;
            existing.SalePrice = salePrice;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            string? syncWarning = null;
            if (request.SyncWithShopify && salePrice > 0m)
            {
                if (ShopifySessionReader.TryGet( _httpContextAccessor, out ShopifySession session ))
                {
                    try
                    {
                        await _shopifyInventory.SetVariantPriceByProductKeyAsync(
                            session.Shop,
                            session.AccessToken,
                            productId,
                            salePrice );
                    }
                    catch (Exception ex)
                    {
                        syncWarning = ex.Message;
                    }
                }
                else
                {
                    syncWarning = "Няма Shopify-кантэксту для абнаўлення цаны.";
                }
            }

            IReadOnlyDictionary<(string ProductId, string VariantId), string> variantTitles =
                await TryLoadVariantTitlesAsync();
            Dictionary<string, string> productNames = await BuildProductNamesAsync( new HashSet<string>( [productId], StringComparer.OrdinalIgnoreCase ) );
            Dictionary<string, string> productAuthors = await TryLoadProductAuthorsAsync( new HashSet<string>( [productId], StringComparer.OrdinalIgnoreCase ) );
            Dictionary<string, string> productTypes = await TryLoadProductTypesAsync( new HashSet<string>( [productId], StringComparer.OrdinalIgnoreCase ) );
            productNames.TryGetValue( productId, out string? productName );
            productAuthors.TryGetValue( productId, out string? productAuthor );
            productTypes.TryGetValue( productId, out string? productType );
            variantTitles.TryGetValue( (productId, variantId), out string? variantTitle );

            decimal shopifyPrice = 0m;
            if (ShopifySessionReader.TryGet( _httpContextAccessor, out ShopifySession shopifySession ))
            {
                Dictionary<string, decimal> prices = await _shopifyInventory.GetVariantPricesByProductKeysAsync(
                    shopifySession.Shop,
                    shopifySession.AccessToken,
                    [(productId, variantId)] );
                prices.TryGetValue( BuildShopifyPriceKey( productId, variantId ), out shopifyPrice );
            }

            SupplierInventoryRow row = new()
            {
                SupplierId = request.SupplierId,
                SupplierName = supplier.Name ?? string.Empty,
                ShopifyProductId = productId,
                ShopifyVariantId = variantId,
                VariantTitle = VariantLegacyDefaults.IsDefaultVariantTitle( variantTitle )
                    ? string.Empty
                    : (variantTitle ?? string.Empty).Trim(),
                ProductName = string.IsNullOrWhiteSpace( productName ) ? productId : productName,
                ProductAuthor = productAuthor ?? string.Empty,
                ProductType = productType ?? string.Empty,
                SupplierPrice = netUnitPrice,
                VatRatePercent = vatRatePercent,
                GrossUnitPrice = grossUnitPrice,
                SupplierIsVatPayer = supplier.isVATPayer,
                HasPriceOverride = true,
                MarginPercent = marginPercent,
                SalePrice = salePrice,
                ShopifyPrice = request.SyncWithShopify && salePrice > 0m && syncWarning is null
                    ? salePrice
                    : shopifyPrice
            };

            if (!string.IsNullOrWhiteSpace( syncWarning ))
            {
                throw new InvalidOperationException( syncWarning );
            }

            return row;
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
                    VatRatePercent = row.VatRatePercent,
                    MarginPercent = row.MarginPercent,
                    SalePrice = row.SalePrice
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

        private async Task<Dictionary<string, decimal>> TryLoadShopifyPricesAsync( HashSet<InventoryLineKey> keys )
        {
            if (!ShopifySessionReader.TryGet( _httpContextAccessor, out ShopifySession session ) || keys.Count == 0)
            {
                return new Dictionary<string, decimal>( StringComparer.OrdinalIgnoreCase );
            }

            try
            {
                return await _shopifyInventory.GetVariantPricesByProductKeysAsync(
                    session.Shop,
                    session.AccessToken,
                    keys.Select( key => (key.ProductId, key.VariantId) ) );
            }
            catch
            {
                return new Dictionary<string, decimal>( StringComparer.OrdinalIgnoreCase );
            }
        }

        private static string BuildShopifyPriceKey( string productId, string variantId ) =>
            string.IsNullOrWhiteSpace( variantId ) ? productId : $"{productId}::{variantId}";

        private static decimal NormalizeCatalogVatRate( decimal value ) =>
            value <= 5.5m ? 5m : 23m;

        private static (decimal NetUnitPrice, decimal VatRatePercent, decimal GrossUnitPrice, decimal MarginPercent, decimal SalePrice, bool HasPriceOverride)
            ResolvePricing(
                InventoryLineKey key,
                decimal supplyUnitPrice,
                decimal supplyVatRatePercent,
                decimal supplyMarginPercent,
                decimal supplySalePrice,
                bool supplierIsVatPayer,
                IReadOnlyDictionary<InventoryLineKey, PriceOverrideRow> priceOverrides )
        {
            decimal netUnitPrice;
            decimal vatRatePercent;
            decimal marginPercent;
            decimal salePrice;
            bool hasPriceOverride = false;

            if (priceOverrides.TryGetValue( key, out PriceOverrideRow? priceOverride ))
            {
                netUnitPrice = priceOverride.NetUnitPrice;
                vatRatePercent = NormalizeCatalogVatRate( priceOverride.VatRatePercent );
                marginPercent = priceOverride.MarginPercent;
                salePrice = priceOverride.SalePrice;
                hasPriceOverride = true;
            }
            else
            {
                vatRatePercent = NormalizeCatalogVatRate( supplyVatRatePercent > 0m ? supplyVatRatePercent : 23m );
                marginPercent = supplyMarginPercent;
                salePrice = supplySalePrice;
                if (supplierIsVatPayer)
                {
                    netUnitPrice = CalcNetUnitPriceFromGross( supplyUnitPrice, vatRatePercent );
                }
                else
                {
                    netUnitPrice = supplyUnitPrice;
                }
            }

            decimal grossUnitPrice = CalcGrossUnitPrice( netUnitPrice, vatRatePercent, supplierIsVatPayer );
            return (netUnitPrice, vatRatePercent, grossUnitPrice, marginPercent, salePrice, hasPriceOverride);
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

        private static decimal ResolveLatestMarginPercent( IEnumerable<SupplyBatch> batches )
        {
            SupplyBatch? latestWithPrice = batches
                .Where( batch => batch.SupplierPrice > 0m )
                .OrderByDescending( batch => batch.SupplyDate )
                .ThenByDescending( batch => batch.SupplyId )
                .FirstOrDefault();
            if (latestWithPrice is not null)
            {
                return latestWithPrice.MarginPercent;
            }

            return batches
                .OrderByDescending( batch => batch.SupplyDate )
                .ThenByDescending( batch => batch.SupplyId )
                .Select( batch => batch.MarginPercent )
                .FirstOrDefault();
        }

        private static decimal ResolveLatestSalePrice( IEnumerable<SupplyBatch> batches )
        {
            SupplyBatch? latestWithPrice = batches
                .Where( batch => batch.SalePrice > 0m )
                .OrderByDescending( batch => batch.SupplyDate )
                .ThenByDescending( batch => batch.SupplyId )
                .FirstOrDefault();
            if (latestWithPrice is not null)
            {
                return latestWithPrice.SalePrice;
            }

            return batches
                .OrderByDescending( batch => batch.SupplyDate )
                .ThenByDescending( batch => batch.SupplyId )
                .Select( batch => batch.SalePrice )
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

        /// <summary>
        /// Net unit cost from supply gross price and VAT rate (same formula as expense product VAT extraction).
        /// </summary>
        private static decimal CalcNetUnitPriceFromGross( decimal grossUnitPrice, decimal vatRatePercent )
        {
            if (grossUnitPrice <= 0m || vatRatePercent <= 0m)
            {
                return Round2( grossUnitPrice );
            }

            decimal rate = vatRatePercent / 100m;
            decimal vatPart = Round2( grossUnitPrice * rate / (1m + rate) );
            return Round2( grossUnitPrice - vatPart );
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
                        VatRatePercent = sp.VatRatePercent,
                        MarginPercent = sp.MarginPercent,
                        SalePrice = sp.SalePrice
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

        private async Task<IReadOnlyDictionary<string, Dictionary<string, string>>> TryLoadVariantIdByTitleAsync()
        {
            try
            {
                return await _variantLookup.GetVariantIdByProductTitleMapCachedAsync();
            }
            catch
            {
                return new Dictionary<string, Dictionary<string, string>>( StringComparer.OrdinalIgnoreCase );
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

            HashSet<string> idLookup = new( StringComparer.OrdinalIgnoreCase );
            foreach (string productId in productIds)
            {
                string normalized = NormalizeProductId( productId );
                if (string.IsNullOrWhiteSpace( normalized )) continue;
                idLookup.Add( normalized );
                idLookup.Add( ShopifyIds.ToProductGid( normalized ) );
            }

            List<string> idList = idLookup.ToList();
            void TryAddName( string rawProductId, string? title )
            {
                string productId = NormalizeProductId( rawProductId );
                if (string.IsNullOrWhiteSpace( productId ) || !productIds.Contains( productId )) return;
                if (names.ContainsKey( productId )) return;
                if (string.IsNullOrWhiteSpace( title )) return;
                names[productId] = title.Trim();
            }

            foreach (var product in await _db.VatReportExpenseProducts
                         .AsNoTracking()
                         .Where( p => idList.Contains( p.ShopifyProductId ) && p.ProductTitle != "" )
                         .Select( p => new { p.ShopifyProductId, p.ProductTitle } )
                         .ToListAsync())
            {
                TryAddName( product.ShopifyProductId, product.ProductTitle );
            }

            foreach (var sale in await _db.VatReportCashSales
                         .AsNoTracking()
                         .Where( s => idList.Contains( s.ShopifyProductId ) && s.ProductTitle != "" )
                         .Select( s => new { s.ShopifyProductId, s.ProductTitle } )
                         .ToListAsync())
            {
                TryAddName( sale.ShopifyProductId, sale.ProductTitle );
            }

            foreach (var item in await _db.VatReportRowItems
                         .AsNoTracking()
                         .Where( i => idList.Contains( i.ShopifyProductId ) && i.ProductTitle != "" )
                         .Select( i => new { i.ShopifyProductId, i.ProductTitle } )
                         .ToListAsync())
            {
                TryAddName( item.ShopifyProductId, item.ProductTitle );
            }

            List<string> missingNameIds = productIds
                .Where( id => !names.ContainsKey( id ) )
                .ToList();
            if (missingNameIds.Count == 0)
            {
                return names;
            }

            try
            {
                Dictionary<string, string> resolved =
                    await _variantLookup.ResolveProductTitlesAsync( missingNameIds );
                foreach (KeyValuePair<string, string> entry in resolved)
                {
                    if (!names.ContainsKey( entry.Key ) && !string.IsNullOrWhiteSpace( entry.Value ))
                    {
                        names[entry.Key] = entry.Value.Trim();
                    }
                }
            }
            catch
            {
                // Optional Shopify lookup; ledger titles above are enough when present.
            }

            return names;
        }

        private async Task<Dictionary<string, string>> TryLoadProductAuthorsAsync( HashSet<string> productIds )
        {
            Dictionary<string, string> authors = new( StringComparer.OrdinalIgnoreCase );
            if (productIds.Count == 0)
            {
                return authors;
            }

            try
            {
                if (_variantLookup.IsCatalogCacheWarm)
                {
                    IReadOnlyDictionary<string, string> catalogAuthors =
                        await _variantLookup.GetProductAuthorByIdMapCachedAsync();
                    foreach (string productId in productIds)
                    {
                        if (catalogAuthors.TryGetValue( productId, out string? author ) &&
                            !string.IsNullOrWhiteSpace( author ))
                        {
                            authors[productId] = author.Trim();
                        }
                    }
                }
            }
            catch
            {
                // Shopify catalog is optional.
            }

            return authors;
        }

        private async Task<Dictionary<string, string>> TryLoadProductTypesAsync( HashSet<string> productIds )
        {
            Dictionary<string, string> types = new( StringComparer.OrdinalIgnoreCase );
            if (productIds.Count == 0)
            {
                return types;
            }

            try
            {
                if (_variantLookup.IsCatalogCacheWarm)
                {
                    IReadOnlyDictionary<string, string> catalogTypes =
                        await _variantLookup.GetProductTypeByIdMapCachedAsync();
                    foreach (string productId in productIds)
                    {
                        if (catalogTypes.TryGetValue( productId, out string? productType ) &&
                            !string.IsNullOrWhiteSpace( productType ))
                        {
                            types[productId] = productType.Trim();
                        }
                    }
                }
            }
            catch
            {
                // Shopify catalog is optional.
            }

            return types;
        }

        private async Task<Dictionary<InventoryLineKey, int>> BuildSoldBySupplierLineFastAsync(
            List<SupplyBatch> supplyBatches )
        {
            ProductSoldAllocation soldAllocation = await _ledger.GetSoldByLineAsync();
            Dictionary<(string ProductId, string VariantId), int> soldByLine = new(
                soldAllocation.SoldByLine,
                ProductVariantKeyComparer.Instance );
            Dictionary<string, int> legacyUnnamedSoldByProduct = new(
                soldAllocation.LegacyUnnamedSoldByProduct,
                StringComparer.OrdinalIgnoreCase );
            return AllocateSoldBySupplierFifo(
                soldByLine,
                legacyUnnamedSoldByProduct,
                supplyBatches );
        }

        private async Task<Dictionary<InventoryLineKey, int>> BuildSoldBySupplierLineFromHistoryAsync(
            List<SupplyBatch> supplyBatches,
            Dictionary<string, string> productNames,
            HashSet<string>? limitToProductIds = null )
        {
            Dictionary<InventoryLineKey, int> soldBySupplierLine = new( InventoryLineKeyComparer.Instance );
            HashSet<string> processedProductIds = new( StringComparer.OrdinalIgnoreCase );

            foreach (string rawProductId in supplyBatches
                         .Select( batch => batch.ShopifyProductId )
                         .Where( id => !string.IsNullOrWhiteSpace( id ) ))
            {
                string normalizedProductId = NormalizeProductId( rawProductId );
                if (string.IsNullOrWhiteSpace( normalizedProductId ) ||
                    !processedProductIds.Add( normalizedProductId ))
                {
                    continue;
                }

                if (limitToProductIds is not null &&
                    !limitToProductIds.Contains( normalizedProductId ))
                {
                    continue;
                }

                string productName;
                if (!productNames.TryGetValue( normalizedProductId, out string? mappedName ) ||
                    string.IsNullOrWhiteSpace( mappedName ))
                {
                    productName = await _ledger.ResolveProductNameForLedgerAsync( normalizedProductId );
                }
                else
                {
                    productName = mappedName;
                }

                List<ProductHistorySaleEvent> sales = await _ledger.GetSaleEventsForProductAsync(
                    normalizedProductId,
                    normalizedVariantFilter: null,
                    filterVariantTitle: null,
                    supplierId: null,
                    productName,
                    matchByProductIdOnly: true );

                Dictionary<(string ProductId, string VariantId), int> soldByLine = new(
                    ProductVariantKeyComparer.Instance );
                Dictionary<string, int> legacyUnnamedSoldByProduct = new( StringComparer.OrdinalIgnoreCase );
                foreach (ProductHistorySaleEvent sale in sales)
                {
                    string variantId = (sale.ShopifyVariantId ?? string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace( variantId ))
                    {
                        (string ProductId, string VariantId) key = (normalizedProductId, variantId);
                        soldByLine[key] = soldByLine.GetValueOrDefault( key ) + sale.Quantity;
                    }
                    else
                    {
                        legacyUnnamedSoldByProduct[normalizedProductId] =
                            legacyUnnamedSoldByProduct.GetValueOrDefault( normalizedProductId ) + sale.Quantity;
                    }
                }

                List<SupplyBatch> productBatches = supplyBatches
                    .Where( batch =>
                        string.Equals(
                            NormalizeProductId( batch.ShopifyProductId ),
                            normalizedProductId,
                            StringComparison.OrdinalIgnoreCase ) )
                    .ToList();

                foreach (KeyValuePair<InventoryLineKey, int> allocated in AllocateSoldBySupplierFifo(
                             soldByLine,
                             legacyUnnamedSoldByProduct,
                             productBatches ))
                {
                    soldBySupplierLine[allocated.Key] =
                        soldBySupplierLine.GetValueOrDefault( allocated.Key ) + allocated.Value;
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

        private static int ComputeQuantityToPay(
            InventoryLineKey key,
            int soldQuantity,
            int paidQuantity,
            Dictionary<InventoryLineKey, int> soldBySupplierLine,
            Dictionary<InventoryLineKey, int> paidBySupplierLine,
            bool useProductTotals )
        {
            int balance = soldQuantity - paidQuantity;
            if (balance <= 0)
            {
                return balance;
            }

            int crossSupplierCredit = ComputeCrossSupplierPaymentCredit(
                key,
                soldBySupplierLine,
                paidBySupplierLine,
                useProductTotals );
            return Math.Max( 0, balance - crossSupplierCredit );
        }

        /// <summary>
        /// Payments to other suppliers for the same product that exceed their FIFO sold qty
        /// can settle this supplier's unpaid balance (e.g. invoice recorded under wrong supplier).
        /// </summary>
        private static int ComputeCrossSupplierPaymentCredit(
            InventoryLineKey key,
            Dictionary<InventoryLineKey, int> soldBySupplierLine,
            Dictionary<InventoryLineKey, int> paidBySupplierLine,
            bool useProductTotals )
        {
            int excessOnOthers = 0;
            HashSet<int> processedOtherSuppliers = new();

            foreach (KeyValuePair<InventoryLineKey, int> entry in paidBySupplierLine)
            {
                if (entry.Key.SupplierId == key.SupplierId)
                {
                    continue;
                }

                if (useProductTotals)
                {
                    if (!string.Equals(
                            entry.Key.ProductId,
                            key.ProductId,
                            StringComparison.OrdinalIgnoreCase ) ||
                        !processedOtherSuppliers.Add( entry.Key.SupplierId ))
                    {
                        continue;
                    }

                    int soldOnOther = SumQuantityForSupplierProduct( soldBySupplierLine, entry.Key );
                    int paidOnOther = SumQuantityForSupplierProduct( paidBySupplierLine, entry.Key );
                    excessOnOthers += Math.Max( 0, paidOnOther - soldOnOther );
                    continue;
                }

                if (!InventoryLineKeyComparer.Instance.Equals( entry.Key, key ))
                {
                    continue;
                }

                int soldOnOtherLine = soldBySupplierLine.GetValueOrDefault( entry.Key );
                excessOnOthers += Math.Max( 0, entry.Value - soldOnOtherLine );
            }

            return excessOnOthers;
        }

        private static SupplyBatch CloneSupplyBatch( SupplyBatch source, int quantity ) =>
            new()
            {
                SupplyId = source.SupplyId,
                SupplyDate = source.SupplyDate,
                SupplierId = source.SupplierId,
                ShopifyProductId = source.ShopifyProductId,
                ShopifyVariantId = source.ShopifyVariantId,
                LineKey = source.LineKey,
                Quantity = quantity,
                SupplierPrice = source.SupplierPrice,
                VatRatePercent = source.VatRatePercent
            };

        /// <summary>
        /// Returns (negative supply qty) reduce the same supplier's recent receipts before FIFO sale allocation.
        /// </summary>
        private static List<SupplyBatch> NormalizeSupplyBatchesForFifo( List<SupplyBatch> supplyBatches )
        {
            List<SupplyBatch> result = new();
            foreach (IGrouping<InventoryLineKey, SupplyBatch> group in supplyBatches
                         .GroupBy( batch => batch.LineKey, InventoryLineKeyComparer.Instance ))
            {
                List<SupplyBatch> pool = new();
                foreach (SupplyBatch batch in group
                             .OrderBy( b => b.SupplyDate )
                             .ThenBy( b => b.SupplyId ))
                {
                    if (batch.Quantity > 0)
                    {
                        pool.Add( CloneSupplyBatch( batch, batch.Quantity ) );
                        continue;
                    }

                    if (batch.Quantity >= 0)
                    {
                        continue;
                    }

                    int toReturn = -batch.Quantity;
                    for (int i = pool.Count - 1; i >= 0 && toReturn > 0; i--)
                    {
                        SupplyBatch target = pool[i];
                        int deduct = Math.Min( toReturn, target.Quantity );
                        target.Quantity -= deduct;
                        toReturn -= deduct;
                    }
                }

                foreach (SupplyBatch batch in pool.Where( b => b.Quantity > 0 ))
                {
                    result.Add( batch );
                }
            }

            return result
                .OrderBy( b => b.SupplyDate )
                .ThenBy( b => b.SupplyId )
                .ToList();
        }

        private static Dictionary<InventoryLineKey, int> AllocateSoldBySupplierFifo(
            Dictionary<(string ProductId, string VariantId), int> soldByLine,
            Dictionary<string, int> legacyUnnamedSoldByProduct,
            List<SupplyBatch> supplyBatches )
        {
            supplyBatches = NormalizeSupplyBatchesForFifo( supplyBatches );
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
            string.Equals(
                ShopifyIds.NormalizeProductId( leftProductId ),
                ShopifyIds.NormalizeProductId( rightProductId ),
                StringComparison.OrdinalIgnoreCase ) &&
            string.Equals(
                ShopifyIds.NormalizeVariantId( leftVariantId ),
                ShopifyIds.NormalizeVariantId( rightVariantId ),
                StringComparison.OrdinalIgnoreCase );

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
                    ProductTitle = p.ProductTitle,
                    Quantity = p.Quantity
                } )
                .ToListAsync();

            return paidProducts
                .GroupBy( p => new InventoryLineKey(
                    p.SupplierId,
                    NormalizeProductId( p.ShopifyProductId ),
                    ProductLedgerService.ResolveEffectiveVariantId(
                        p.ShopifyProductId,
                        NormalizeVariantId( p.ShopifyVariantId ),
                        VatReportHelpers.ExtractVariantTitleFromProductLineTitle( p.ProductTitle ),
                        variantIdByTitle,
                        defaultVariantByProduct,
                        legacySaleVariantByProduct ) ) )
                .ToDictionary( g => g.Key, g => g.Sum( x => x.Quantity ), InventoryLineKeyComparer.Instance );
        }

        private static int ResolveQuantityInStock(
            string productId,
            string variantId,
            bool useProductTotals,
            IReadOnlyDictionary<(string ProductId, string VariantId), int> stockByLine,
            IReadOnlyDictionary<string, string> defaultVariantByProduct )
        {
            if (stockByLine.TryGetValue( (productId, variantId), out int quantity ))
            {
                return quantity;
            }

            if (string.IsNullOrWhiteSpace( variantId ) &&
                defaultVariantByProduct.TryGetValue( productId, out string? defaultVariantId ) &&
                !string.IsNullOrWhiteSpace( defaultVariantId ) &&
                stockByLine.TryGetValue( (productId, defaultVariantId), out quantity ))
            {
                return quantity;
            }

            if (useProductTotals)
            {
                int total = stockByLine
                    .Where( entry => string.Equals( entry.Key.ProductId, productId, StringComparison.OrdinalIgnoreCase ) )
                    .Sum( entry => entry.Value );
                if (total > 0)
                {
                    return total;
                }
            }

            return stockByLine.TryGetValue( (productId, string.Empty), out quantity ) ? quantity : 0;
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
            public decimal MarginPercent { get; set; }
            public decimal SalePrice { get; set; }
        }

        private sealed class PaidProductRow
        {
            public int SupplierId { get; set; }
            public string ShopifyProductId { get; set; } = string.Empty;
            public string ShopifyVariantId { get; set; } = string.Empty;
            public string ProductTitle { get; set; } = string.Empty;
            public int Quantity { get; set; }
        }

        private sealed class PriceOverrideRow
        {
            public int SupplierId { get; set; }
            public string ShopifyProductId { get; set; } = string.Empty;
            public string ShopifyVariantId { get; set; } = string.Empty;
            public decimal NetUnitPrice { get; set; }
            public decimal VatRatePercent { get; set; }
            public decimal MarginPercent { get; set; }
            public decimal SalePrice { get; set; }
        }
    }
}
