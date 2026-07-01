using System.Globalization;
using System.Text.Json;
using backend.Data;
using Microsoft.EntityFrameworkCore;

namespace backend.Services.Shopify;

/// <summary>
/// Shopify GraphQL order fetching for VAT reports and inventory sales cache.
/// </summary>
public class ShopifyOrderFetchService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AppDbContext _db;
    private readonly ShopifyGraphqlClient _graphql;
    private readonly ILogger<ShopifyOrderFetchService> _logger;

    public ShopifyOrderFetchService(
        IHttpContextAccessor httpContextAccessor,
        AppDbContext db,
        ShopifyGraphqlClient graphql,
        ILogger<ShopifyOrderFetchService> logger )
    {
        _httpContextAccessor = httpContextAccessor;
        _db = db;
        _graphql = graphql;
        _logger = logger;
    }

    public Task<List<ShopifyOrderDto>> FetchOrdersForPolandAsync( int year, int month ) =>
        FetchOrdersAsync( year, month, ShopifyOrderScope.Poland );

    public Task<List<ShopifyOrderDto>> FetchOrdersForForeignAsync( int year, int month ) =>
        FetchOrdersAsync( year, month, ShopifyOrderScope.Foreign );

    public async Task<(List<ShopifyOrderDto> Poland, List<ShopifyOrderDto> Foreign)> FetchOrdersForReportMonthAsync(
        int year,
        int month )
    {
        (string shop, string accessToken) = GetShopifyCredentials();
        List<ShopifyOrderDto> poland = await FetchOrdersWithClientAsync(
            shop, accessToken, year, month, ShopifyOrderScope.Poland );
        List<ShopifyOrderDto> foreign = await FetchOrdersWithClientAsync(
            shop, accessToken, year, month, ShopifyOrderScope.Foreign );
        return (poland, foreign);
    }

    public async Task<Dictionary<string, int>> GetSoldQuantitiesByProductFromShopifyAsync()
    {
        Dictionary<(string ProductId, string VariantId), int> byVariant =
            await GetSoldQuantitiesByProductVariantFromShopifyAsync();
        Dictionary<string, int> soldByProduct = new( StringComparer.OrdinalIgnoreCase );
        foreach (KeyValuePair<(string ProductId, string VariantId), int> entry in byVariant)
        {
            soldByProduct[entry.Key.ProductId] = soldByProduct.GetValueOrDefault( entry.Key.ProductId ) + entry.Value;
        }

        return soldByProduct;
    }

    public async Task<Dictionary<(string ProductId, string VariantId), int>> GetSoldQuantitiesByProductVariantForMonthAsync(
        int year,
        int month )
    {
        Dictionary<(string ProductId, string VariantId), int> soldByLine =
            new( ProductVariantKeyComparer.Instance );

        (string shop, string accessToken) = GetShopifyCredentials();
        List<ShopifyOrderDto> poland = await FetchOrdersWithClientAsync(
            shop, accessToken, year, month, ShopifyOrderScope.Poland );
        List<ShopifyOrderDto> foreign = await FetchOrdersWithClientAsync(
            shop, accessToken, year, month, ShopifyOrderScope.Foreign );
        AddOrderItemsToSoldVariantMap( poland, soldByLine );
        AddOrderItemsToSoldVariantMap( foreign, soldByLine );

        return soldByLine;
    }

    public async Task<Dictionary<(string ProductId, string VariantId), int>> GetSoldQuantitiesByProductVariantFromShopifyAsync()
    {
        DateOnly? earliestSupplyDate = await _db.Supplies
            .AsNoTracking()
            .MinAsync( s => (DateOnly?)s.Date );
        if (!earliestSupplyDate.HasValue)
        {
            return new Dictionary<(string ProductId, string VariantId), int>( ProductVariantKeyComparer.Instance );
        }

        DateOnly startMonth = new( earliestSupplyDate.Value.Year, earliestSupplyDate.Value.Month, 1 );
        DateOnly endMonth = DateOnly.FromDateTime( DateTime.UtcNow );
        Dictionary<(string ProductId, string VariantId), int> soldByLine =
            new( ProductVariantKeyComparer.Instance );

        (string shop, string accessToken) = GetShopifyCredentials();

        for (DateOnly monthCursor = startMonth; monthCursor <= endMonth; monthCursor = monthCursor.AddMonths( 1 ))
        {
            List<ShopifyOrderDto> poland = await FetchOrdersWithClientAsync(
                shop, accessToken, monthCursor.Year, monthCursor.Month, ShopifyOrderScope.Poland );
            List<ShopifyOrderDto> foreign = await FetchOrdersWithClientAsync(
                shop, accessToken, monthCursor.Year, monthCursor.Month, ShopifyOrderScope.Foreign );
            AddOrderItemsToSoldVariantMap( poland, soldByLine );
            AddOrderItemsToSoldVariantMap( foreign, soldByLine );
        }

        return soldByLine;
    }

    public async Task<Dictionary<string, int>> GetSoldQuantitiesFromShopifySinceAsync( DateTime sinceUtc )
    {
        DateTime toUtc = DateTime.UtcNow;
        if (sinceUtc >= toUtc)
        {
            return new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
        }

        DateOnly startMonth = DateOnly.FromDateTime( sinceUtc );
        DateOnly endMonth = DateOnly.FromDateTime( toUtc );
        Dictionary<(string ProductId, string VariantId), int> soldByLine =
            new( ProductVariantKeyComparer.Instance );

        (string shop, string accessToken) = GetShopifyCredentials();

        for (DateOnly monthCursor = new DateOnly( startMonth.Year, startMonth.Month, 1 );
             monthCursor <= endMonth;
             monthCursor = monthCursor.AddMonths( 1 ))
        {
            List<ShopifyOrderDto> poland = await FetchOrdersWithClientAsync(
                shop, accessToken, monthCursor.Year, monthCursor.Month, ShopifyOrderScope.Poland );
            List<ShopifyOrderDto> foreign = await FetchOrdersWithClientAsync(
                shop, accessToken, monthCursor.Year, monthCursor.Month, ShopifyOrderScope.Foreign );
            AddOrdersToSoldVariantMapSince( poland, sinceUtc, soldByLine );
            AddOrdersToSoldVariantMapSince( foreign, sinceUtc, soldByLine );
        }

        Dictionary<string, int> soldByProduct = new( StringComparer.OrdinalIgnoreCase );
        foreach (KeyValuePair<(string ProductId, string VariantId), int> entry in soldByLine)
        {
            soldByProduct[entry.Key.ProductId] = soldByProduct.GetValueOrDefault( entry.Key.ProductId ) + entry.Value;
        }

        return soldByProduct;
    }

    public async Task<Dictionary<string, ForeignDeliveryInfo>> FetchForeignDeliveryInfoAsync( List<string> orderIds )
    {
        Dictionary<string, ForeignDeliveryInfo> result = new( StringComparer.OrdinalIgnoreCase );
        if (orderIds.Count == 0) return result;

        if (!ShopifySessionReader.TryGet( _httpContextAccessor, out ShopifySession session )) return result;

        const int batchSize = 50;
        for (int i = 0; i < orderIds.Count; i += batchSize)
        {
            List<string> batch = orderIds.Skip( i ).Take( batchSize ).ToList();
            string[] gids = batch.Select( id => $"gid://shopify/Order/{id}" ).ToArray();
            (bool success, JsonDocument? json, string? error) = await _graphql.TryExecuteAsync(
                session.Shop,
                session.AccessToken,
                ShopifyGraphqlQueries.OrderDeliveryNodes,
                new { ids = gids }
            );
            if (!success || json is null)
            {
                _logger.LogWarning( "Shopify delivery info request failed: {Error}", error );
                continue;
            }

            using (json)
            {
            if (!json.RootElement.TryGetProperty( "data", out JsonElement dataEl ) ||
                !dataEl.TryGetProperty( "nodes", out JsonElement nodesEl ) ||
                nodesEl.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning( "Shopify delivery info response has unexpected shape." );
                continue;
            }

            foreach (JsonElement node in nodesEl.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object) continue;
                if (!node.TryGetProperty( "id", out JsonElement idEl ) || idEl.ValueKind != JsonValueKind.String) continue;
                string orderId = ShopifyIds.NormalizeOrderId( idEl.GetString() ?? string.Empty );
                if (string.IsNullOrWhiteSpace( orderId )) continue;

                JsonElement shippingAddr = node.TryGetProperty( "shippingAddress", out JsonElement shippingEl ) &&
                                           shippingEl.ValueKind == JsonValueKind.Object
                    ? shippingEl
                    : default;
                JsonElement billingAddr = node.TryGetProperty( "billingAddress", out JsonElement billingEl ) &&
                                          billingEl.ValueKind == JsonValueKind.Object
                    ? billingEl
                    : default;
                JsonElement addr = shippingAddr.ValueKind == JsonValueKind.Object ? shippingAddr : billingAddr;
                string firstName = ReadString( addr, "firstName" );
                string lastName = ReadString( addr, "lastName" );
                string name = $"{firstName} {lastName}".Trim();
                result[orderId] = new ForeignDeliveryInfo
                {
                    Name = name,
                    ShippingAddress = FormatAddress( shippingAddr ),
                    BillingAddress = FormatAddress( billingAddr ),
                    ShippingCountryCode = ReadCountryCode( shippingAddr ),
                    BillingCountryCode = ReadCountryCode( billingAddr )
                };
            }
            }
        }

        return result;
    }

    public async Task<Dictionary<string, ShopifyOrderDto>> FetchOrdersByIdsAsync( IEnumerable<string> orderIds )
    {
        Dictionary<string, ShopifyOrderDto> result = new( StringComparer.OrdinalIgnoreCase );
        List<string> ids = orderIds
            .Select( id => ShopifyIds.NormalizeOrderId( id ) )
            .Where( id => !string.IsNullOrWhiteSpace( id ) )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .ToList();
        if (ids.Count == 0)
        {
            return result;
        }

        (string shop, string accessToken) = GetShopifyCredentials();

        const int batchSize = 50;
        for (int i = 0; i < ids.Count; i += batchSize)
        {
            List<string> batch = ids.Skip( i ).Take( batchSize ).ToList();
            string[] gids = batch.Select( id => $"gid://shopify/Order/{id}" ).ToArray();
            (bool success, JsonDocument? json, string? error) = await _graphql.TryExecuteAsync(
                shop,
                accessToken,
                ShopifyGraphqlQueries.OrderLineItemNodes,
                new { ids = gids }
            );
            if (!success || json is null)
            {
                _logger.LogWarning( "Shopify order line items request failed: {Error}", error );
                continue;
            }

            using (json)
            {
                if (!json.RootElement.TryGetProperty( "data", out JsonElement dataEl ) ||
                    !dataEl.TryGetProperty( "nodes", out JsonElement nodesEl ) ||
                    nodesEl.ValueKind != JsonValueKind.Array)
                {
                    _logger.LogWarning( "Shopify order line items response has unexpected shape." );
                    continue;
                }

                foreach (JsonElement node in nodesEl.EnumerateArray())
                {
                    if (node.ValueKind != JsonValueKind.Object) continue;
                    if (!node.TryGetProperty( "id", out JsonElement idEl ) || idEl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string orderId = ShopifyIds.NormalizeOrderId( idEl.GetString() ?? string.Empty );
                    if (string.IsNullOrWhiteSpace( orderId ))
                    {
                        continue;
                    }

                    if (ShouldExcludeOrderFromReports( node ))
                    {
                        result[orderId] = new ShopifyOrderDto
                        {
                            OrderId = orderId,
                            CreatedAtUtc = DateTime.UtcNow,
                            Items = []
                        };
                        continue;
                    }

                    DateTime createdAtUtc = DateTime.UtcNow;
                    TryParseCreatedAt( node, out createdAtUtc, out _ );

                    result[orderId] = new ShopifyOrderDto
                    {
                        OrderId = orderId,
                        CreatedAtUtc = createdAtUtc,
                        Items = ParseLineItems( node )
                    };
                }
            }
        }

        return result;
    }

    private Task<List<ShopifyOrderDto>> FetchOrdersAsync( int year, int month, ShopifyOrderScope scope )
    {
        (string shop, string accessToken) = GetShopifyCredentials();
        return FetchOrdersWithClientAsync( shop, accessToken, year, month, scope );
    }

    private async Task<List<ShopifyOrderDto>> FetchOrdersWithClientAsync(
        string shop,
        string accessToken,
        int year,
        int month,
        ShopifyOrderScope scope )
    {
        string queryFilter = scope switch
        {
            ShopifyOrderScope.Poland => BuildPolandQueryFilter( year, month ),
            ShopifyOrderScope.Foreign => "status:any",
            _ => throw new ArgumentOutOfRangeException( nameof( scope ) )
        };

        TimeZoneInfo polandTz = GetPolandTimeZone();
        List<ShopifyOrderDto> result = new();
        string? afterCursor = null;
        bool hasNextPage;

        do
        {
            using JsonDocument json = await _graphql.ExecuteAsync(
                shop,
                accessToken,
                ShopifyGraphqlQueries.OrdersPage,
                new { query = queryFilter, after = afterCursor }
            );
            JsonElement orders = json.RootElement.GetProperty( "data" ).GetProperty( "orders" );

            foreach (JsonElement edge in orders.GetProperty( "edges" ).EnumerateArray())
            {
                JsonElement node = edge.GetProperty( "node" );
                OrderShippingContext shipping = ParseShippingContext( node );

                if (scope == ShopifyOrderScope.Poland)
                {
                    if (!IsPolandDelivery( shipping )) continue;
                }
                else
                {
                    if (IsPolandPickup( shipping )) continue;
                    string countryCode = string.IsNullOrWhiteSpace( shipping.ShippingCountryCode )
                        ? shipping.BillingCountryCode
                        : shipping.ShippingCountryCode;
                    if (string.Equals( countryCode, "PL", StringComparison.OrdinalIgnoreCase )) continue;
                }

                string orderId = node.TryGetProperty( "id", out JsonElement idEl ) && idEl.ValueKind == JsonValueKind.String
                    ? ShopifyIds.NormalizeOrderId( idEl.GetString() ?? string.Empty )
                    : string.Empty;
                if (string.IsNullOrWhiteSpace( orderId )) continue;

                if (ShouldExcludeOrderFromReports( node )) continue;

                string orderNumber = node.TryGetProperty( "name", out JsonElement nameEl ) && nameEl.ValueKind == JsonValueKind.String
                    ? (nameEl.GetString() ?? orderId)
                    : orderId;

                if (!TryParseCreatedAt( node, out DateTime createdAt, out DateTimeOffset createdAtOffset )) continue;

                if (scope == ShopifyOrderScope.Poland)
                {
                    DateTime createdAtPoland = TimeZoneInfo.ConvertTimeFromUtc( createdAt, polandTz );
                    if (createdAtPoland.Year != year || createdAtPoland.Month != month) continue;
                }
                else if (!IsInRequestedMonth( year, month, createdAt, createdAtOffset, polandTz )) continue;

                List<ShopifyLineItemDto> items = ParseLineItems( node );
                if (items.Count == 0) continue;

                decimal shippingGross = SumShippingGross( node );
                ShopifyOrderDto dto = new()
                {
                    OrderId = orderId,
                    OrderNumber = orderNumber,
                    CreatedAtUtc = createdAt,
                    CurrentTotalGross = Round2( ReadMoney( node, "currentTotalPriceSet" ) ),
                    ShippingGross = Round2( shippingGross ),
                    Items = items
                };

                if (scope == ShopifyOrderScope.Foreign)
                {
                    dto.CountryCode = string.IsNullOrWhiteSpace( shipping.ShippingCountryCode )
                        ? shipping.BillingCountryCode
                        : shipping.ShippingCountryCode;
                }

                result.Add( dto );
            }

            JsonElement pageInfo = orders.GetProperty( "pageInfo" );
            hasNextPage = pageInfo.GetProperty( "hasNextPage" ).GetBoolean();
            afterCursor = pageInfo.GetProperty( "endCursor" ).GetString();
        } while (hasNextPage && !string.IsNullOrWhiteSpace( afterCursor ));

        return result;
    }

    private static string BuildPolandQueryFilter( int year, int month )
    {
        (DateTime from, DateTime to) = GetPolandMonthBoundsUtc( year, month );
        return $"status:any created_at:>={from:yyyy-MM-ddTHH:mm:ssZ} created_at:<{to:yyyy-MM-ddTHH:mm:ssZ}";
    }

    private (string Shop, string AccessToken) GetShopifyCredentials()
    {
        ShopifySession session = ShopifySessionReader.Require(
            _httpContextAccessor,
            "Няма Shopify-кантэксту для генерацыі справаздачы."
        );
        return (session.Shop, session.AccessToken);
    }

    private static OrderShippingContext ParseShippingContext( JsonElement node )
    {
        string shippingCountryCode = ReadCountryCode( node, "shippingAddress" );
        string billingCountryCode = ReadCountryCode( node, "billingAddress" );
        bool hasPickupShippingLine = false;
        bool hasZeroShippingLineWithTitle = false;

        if (node.TryGetProperty( "shippingLines", out JsonElement shippingLinesEl ) &&
            shippingLinesEl.ValueKind == JsonValueKind.Object &&
            shippingLinesEl.TryGetProperty( "nodes", out JsonElement shippingNodesEl ) &&
            shippingNodesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement shippingNode in shippingNodesEl.EnumerateArray())
            {
                if (!shippingNode.TryGetProperty( "title", out JsonElement shippingTitleEl ) ||
                    shippingTitleEl.ValueKind != JsonValueKind.String) continue;

                string shippingTitle = (shippingTitleEl.GetString() ?? string.Empty).ToLowerInvariant();
                decimal shippingLineAmount = ReadMoney( shippingNode, "originalPriceSet" );
                if (!string.IsNullOrWhiteSpace( shippingTitle ) && shippingLineAmount == 0m)
                {
                    hasZeroShippingLineWithTitle = true;
                }

                if (IsPickupShippingTitle( shippingTitle ))
                {
                    hasPickupShippingLine = true;
                    break;
                }
            }
        }

        return new OrderShippingContext(
            shippingCountryCode,
            billingCountryCode,
            hasPickupShippingLine,
            hasZeroShippingLineWithTitle
        );
    }

    private static string ReadCountryCodeFromAddress( JsonElement addressEl )
    {
        if (addressEl.TryGetProperty( "countryCode", out JsonElement countryCodeEl ) &&
            countryCodeEl.ValueKind == JsonValueKind.String)
        {
            string code = countryCodeEl.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace( code )) return code;
        }

        if (addressEl.TryGetProperty( "countryCodeV2", out JsonElement countryCodeV2El ) &&
            countryCodeV2El.ValueKind == JsonValueKind.String)
        {
            return countryCodeV2El.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static bool ShouldExcludeOrderFromReports( JsonElement node )
    {
        if (node.TryGetProperty( "cancelledAt", out JsonElement cancelledAtEl ) &&
            cancelledAtEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace( cancelledAtEl.GetString() ))
        {
            return true;
        }

        if (node.TryGetProperty( "displayFinancialStatus", out JsonElement statusEl ) &&
            statusEl.ValueKind == JsonValueKind.String)
        {
            string status = statusEl.GetString() ?? string.Empty;
            if (string.Equals( status, "REFUNDED", StringComparison.OrdinalIgnoreCase ) ||
                string.Equals( status, "VOIDED", StringComparison.OrdinalIgnoreCase ))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPolandDelivery( OrderShippingContext shipping ) =>
        string.Equals( shipping.ShippingCountryCode, "PL", StringComparison.OrdinalIgnoreCase ) ||
        (string.IsNullOrWhiteSpace( shipping.ShippingCountryCode ) &&
         string.Equals( shipping.BillingCountryCode, "PL", StringComparison.OrdinalIgnoreCase )) ||
        shipping.HasPickupShippingLine ||
        (string.IsNullOrWhiteSpace( shipping.ShippingCountryCode ) && shipping.HasZeroShippingLineWithTitle );

    private static bool IsPolandPickup( OrderShippingContext shipping ) =>
        shipping.HasPickupShippingLine ||
        (string.IsNullOrWhiteSpace( shipping.ShippingCountryCode ) && shipping.HasZeroShippingLineWithTitle );

    private static bool IsPickupShippingTitle( string shippingTitleLower ) =>
        shippingTitleLower.Contains( "pickup" ) ||
        shippingTitleLower.Contains( "pick up" ) ||
        shippingTitleLower.Contains( "pick-up" ) ||
        shippingTitleLower.Contains( "odbiór" ) ||
        shippingTitleLower.Contains( "odbior" ) ||
        shippingTitleLower.Contains( "odbiór w sklepie" ) ||
        shippingTitleLower.Contains( "самовывоз" ) ||
        shippingTitleLower.Contains( "самовивіз" );

    private static bool IsInRequestedMonth(
        int year,
        int month,
        DateTime createdAtUtc,
        DateTimeOffset createdAtOffset,
        TimeZoneInfo polandTz )
    {
        DateTime createdAtPoland = TimeZoneInfo.ConvertTimeFromUtc( createdAtUtc, polandTz );
        return createdAtPoland.Year == year && createdAtPoland.Month == month ||
               createdAtUtc.Year == year && createdAtUtc.Month == month ||
               createdAtOffset.Year == year && createdAtOffset.Month == month;
    }

    private static bool TryParseCreatedAt(
        JsonElement node,
        out DateTime createdAtUtc,
        out DateTimeOffset createdAtOffset )
    {
        createdAtUtc = DateTime.UtcNow;
        createdAtOffset = DateTimeOffset.UtcNow;

        if (!node.TryGetProperty( "createdAt", out JsonElement createdAtEl ) ||
            createdAtEl.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        if (DateTimeOffset.TryParse(
                createdAtEl.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset parsedOffset ))
        {
            createdAtOffset = parsedOffset;
            createdAtUtc = parsedOffset.UtcDateTime;
            return true;
        }

        if (DateTime.TryParse(
                createdAtEl.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal,
                out DateTime parsedUtc ))
        {
            createdAtUtc = parsedUtc;
            createdAtOffset = new DateTimeOffset( parsedUtc, TimeSpan.Zero );
            return true;
        }

        return true;
    }

    private static List<ShopifyLineItemDto> ParseLineItems( JsonElement node )
    {
        List<ShopifyLineItemDto> items = new();
        if (!node.TryGetProperty( "lineItems", out JsonElement lineItemsEl ) ||
            lineItemsEl.ValueKind != JsonValueKind.Object ||
            !lineItemsEl.TryGetProperty( "nodes", out JsonElement itemNodesEl ) ||
            itemNodesEl.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (JsonElement itemNode in itemNodesEl.EnumerateArray())
        {
            int originalQuantity = itemNode.TryGetProperty( "quantity", out JsonElement qtyEl ) &&
                                   qtyEl.ValueKind == JsonValueKind.Number &&
                                   qtyEl.TryGetInt32( out int parsedQty )
                ? parsedQty
                : 0;
            int currentQuantity = originalQuantity;
            if (itemNode.TryGetProperty( "currentQuantity", out JsonElement currentQtyEl ) &&
                currentQtyEl.ValueKind == JsonValueKind.Number &&
                currentQtyEl.TryGetInt32( out int parsedCurrentQty ))
            {
                currentQuantity = parsedCurrentQty;
            }

            if (currentQuantity <= 0) continue;

            string title = itemNode.TryGetProperty( "title", out JsonElement titleEl ) &&
                           titleEl.ValueKind == JsonValueKind.String
                ? (titleEl.GetString() ?? string.Empty)
                : string.Empty;

            (string productId, string productType) = ParseProductFromLineItem( itemNode );
            (string variantId, string variantTitle) = ParseVariantFromLineItem( itemNode );
            decimal unitPrice = ReadMoney( itemNode, "originalUnitPriceSet" );
            decimal originalTotal = ReadMoney( itemNode, "originalTotalSet" );
            decimal discountedTotal = ReadMoney( itemNode, "discountedTotalSet" );
            decimal lineTotalGross = originalTotal > 0m ? originalTotal : unitPrice * originalQuantity;
            if (lineTotalGross <= 0m && discountedTotal > 0m)
            {
                lineTotalGross = discountedTotal;
            }

            if (lineTotalGross <= 0m) continue;
            if (unitPrice <= 0m)
            {
                unitPrice = originalQuantity > 0 ? Round2( lineTotalGross / originalQuantity ) : 0m;
            }

            decimal allocatedDiscountTotal = SumDiscountAllocations( itemNode );
            if (allocatedDiscountTotal > 0m)
            {
                lineTotalGross = Math.Max( 0m, lineTotalGross - allocatedDiscountTotal );
            }
            else if (discountedTotal > 0m && discountedTotal < lineTotalGross)
            {
                lineTotalGross = discountedTotal;
            }

            if (originalQuantity > 0 && currentQuantity < originalQuantity)
            {
                lineTotalGross = Round2( lineTotalGross * currentQuantity / originalQuantity );
                unitPrice = Round2( lineTotalGross / currentQuantity );
            }

            if (lineTotalGross <= 0m) continue;

            items.Add( new ShopifyLineItemDto
            {
                ShopifyProductId = productId,
                ShopifyVariantId = variantId,
                VariantTitle = variantTitle,
                Quantity = currentQuantity,
                UnitPrice = unitPrice,
                LineTotalGross = Round2( lineTotalGross ),
                ProductType = productType,
                Title = title
            } );
        }

        return items;
    }

    private static (string ProductId, string ProductType) ParseProductFromLineItem( JsonElement itemNode )
    {
        string productId = string.Empty;
        string productType = string.Empty;

        if (itemNode.TryGetProperty( "product", out JsonElement lineProductEl ) &&
            lineProductEl.ValueKind == JsonValueKind.Object)
        {
            if (lineProductEl.TryGetProperty( "id", out JsonElement lineProductIdEl ) &&
                lineProductIdEl.ValueKind == JsonValueKind.String)
            {
                productId = ShopifyIds.NormalizeProductId( lineProductIdEl.GetString() ?? string.Empty );
            }

            if (lineProductEl.TryGetProperty( "productType", out JsonElement lineProductTypeEl ) &&
                lineProductTypeEl.ValueKind == JsonValueKind.String)
            {
                productType = lineProductTypeEl.GetString() ?? string.Empty;
            }
        }

        if ((string.IsNullOrWhiteSpace( productType ) || string.IsNullOrWhiteSpace( productId )) &&
            itemNode.TryGetProperty( "variant", out JsonElement variantEl ) &&
            variantEl.ValueKind == JsonValueKind.Object &&
            variantEl.TryGetProperty( "product", out JsonElement variantProductEl ) &&
            variantProductEl.ValueKind == JsonValueKind.Object)
        {
            if (string.IsNullOrWhiteSpace( productId ) &&
                variantProductEl.TryGetProperty( "id", out JsonElement variantProductIdEl ) &&
                variantProductIdEl.ValueKind == JsonValueKind.String)
            {
                productId = ShopifyIds.NormalizeProductId( variantProductIdEl.GetString() ?? string.Empty );
            }

            if (string.IsNullOrWhiteSpace( productType ) &&
                variantProductEl.TryGetProperty( "productType", out JsonElement variantProductTypeEl ) &&
                variantProductTypeEl.ValueKind == JsonValueKind.String)
            {
                productType = variantProductTypeEl.GetString() ?? string.Empty;
            }
        }

        return (productId, productType);
    }

    private static (string VariantId, string VariantTitle) ParseVariantFromLineItem( JsonElement itemNode )
    {
        if (!itemNode.TryGetProperty( "variant", out JsonElement variantEl ) ||
            variantEl.ValueKind != JsonValueKind.Object)
        {
            return (string.Empty, string.Empty);
        }

        string variantId = string.Empty;
        if (variantEl.TryGetProperty( "id", out JsonElement variantIdEl ) &&
            variantIdEl.ValueKind == JsonValueKind.String)
        {
            variantId = ShopifyIds.NormalizeVariantId( variantIdEl.GetString() ?? string.Empty );
        }

        string variantTitle = string.Empty;
        if (variantEl.TryGetProperty( "title", out JsonElement variantTitleEl ) &&
            variantTitleEl.ValueKind == JsonValueKind.String)
        {
            variantTitle = (variantTitleEl.GetString() ?? string.Empty).Trim();
        }

        return (variantId, variantTitle);
    }

    private static decimal SumDiscountAllocations( JsonElement itemNode )
    {
        decimal allocatedDiscountTotal = 0m;
        if (!itemNode.TryGetProperty( "discountAllocations", out JsonElement discountAllocationsEl ) ||
            discountAllocationsEl.ValueKind != JsonValueKind.Array)
        {
            return allocatedDiscountTotal;
        }

        foreach (JsonElement allocationEl in discountAllocationsEl.EnumerateArray())
        {
            if (allocationEl.TryGetProperty( "allocatedAmountSet", out JsonElement amountSetEl ) &&
                amountSetEl.ValueKind == JsonValueKind.Object &&
                amountSetEl.TryGetProperty( "shopMoney", out JsonElement shopMoneyEl ) &&
                shopMoneyEl.ValueKind == JsonValueKind.Object &&
                shopMoneyEl.TryGetProperty( "amount", out JsonElement amountEl ) &&
                amountEl.ValueKind == JsonValueKind.String &&
                decimal.TryParse(
                    amountEl.GetString(),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out decimal parsedAllocation))
            {
                allocatedDiscountTotal += parsedAllocation;
            }
        }

        return allocatedDiscountTotal;
    }

    private static decimal SumShippingGross( JsonElement node )
    {
        decimal shippingGross = 0m;
        if (!node.TryGetProperty( "shippingLines", out JsonElement shippingLinesEl ) ||
            shippingLinesEl.ValueKind != JsonValueKind.Object ||
            !shippingLinesEl.TryGetProperty( "nodes", out JsonElement shippingNodesEl ) ||
            shippingNodesEl.ValueKind != JsonValueKind.Array)
        {
            return shippingGross;
        }

        foreach (JsonElement shippingNode in shippingNodesEl.EnumerateArray())
        {
            decimal currentPrice = ReadMoney( shippingNode, "currentDiscountedPriceSet" );
            if (currentPrice > 0m)
            {
                shippingGross += currentPrice;
                continue;
            }

            decimal discountedPrice = ReadMoney( shippingNode, "discountedPriceSet" );
            if (discountedPrice > 0m)
            {
                shippingGross += discountedPrice;
                continue;
            }

            shippingGross += ReadMoney( shippingNode, "originalPriceSet" );
        }

        return shippingGross;
    }

    private static string ReadCountryCode( JsonElement node, string addressProperty )
    {
        if (!node.TryGetProperty( addressProperty, out JsonElement addressEl ) ||
            addressEl.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return ReadCountryCodeFromAddress( addressEl );
    }

    private static void AddOrdersToSoldVariantMapSince(
        List<ShopifyOrderDto> orders,
        DateTime sinceUtc,
        Dictionary<(string ProductId, string VariantId), int> soldByLine )
    {
        foreach (ShopifyOrderDto order in orders)
        {
            if (order.CreatedAtUtc <= sinceUtc) continue;
            AddOrderItemsToSoldVariantMap( new List<ShopifyOrderDto> { order }, soldByLine );
        }
    }

    private static void AddOrderItemsToSoldVariantMap(
        List<ShopifyOrderDto> orders,
        Dictionary<(string ProductId, string VariantId), int> soldByLine )
    {
        foreach (ShopifyOrderDto order in orders)
        {
            foreach (ShopifyLineItemDto item in order.Items)
            {
                if (item.Quantity <= 0) continue;
                string productId = ShopifyIds.NormalizeProductId( item.ShopifyProductId ).Trim();
                if (string.IsNullOrWhiteSpace( productId )) continue;
                string variantId = ShopifyIds.NormalizeVariantId( item.ShopifyVariantId ).Trim();
                (string ProductId, string VariantId) key = (productId, variantId);
                soldByLine[key] = soldByLine.GetValueOrDefault( key ) + item.Quantity;
            }
        }
    }

    private static void AddOrdersToSoldMapSince(
        List<ShopifyOrderDto> orders,
        DateTime sinceUtc,
        Dictionary<string, int> soldByProduct )
    {
        foreach (ShopifyOrderDto order in orders)
        {
            if (order.CreatedAtUtc <= sinceUtc) continue;
            AddOrderItemsToSoldMap( new List<ShopifyOrderDto> { order }, soldByProduct );
        }
    }

    private static void AddOrderItemsToSoldMap(
        List<ShopifyOrderDto> orders,
        Dictionary<string, int> soldByProduct )
    {
        foreach (ShopifyOrderDto order in orders)
        {
            foreach (ShopifyLineItemDto item in order.Items)
            {
                if (item.Quantity <= 0) continue;
                string productId = ShopifyIds.NormalizeProductId( item.ShopifyProductId ).Trim();
                if (string.IsNullOrWhiteSpace( productId )) continue;
                soldByProduct[productId] = soldByProduct.GetValueOrDefault( productId ) + item.Quantity;
            }
        }
    }

    private static TimeZoneInfo GetPolandTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById( "Europe/Warsaw" );
        }
        catch
        {
            return TimeZoneInfo.FindSystemTimeZoneById( "Central European Standard Time" );
        }
    }

    private static (DateTime fromUtc, DateTime toUtc) GetPolandMonthBoundsUtc( int year, int month )
    {
        TimeZoneInfo polandTz = GetPolandTimeZone();
        DateTime localFrom = new( year, month, 1, 0, 0, 0, DateTimeKind.Unspecified );
        DateTime localTo = localFrom.AddMonths( 1 );
        DateTime fromUtc = TimeZoneInfo.ConvertTimeToUtc( localFrom, polandTz );
        DateTime toUtc = TimeZoneInfo.ConvertTimeToUtc( localTo, polandTz );
        return (fromUtc, toUtc);
    }

    private static decimal ReadMoney( JsonElement node, string setProperty )
    {
        if (!node.TryGetProperty( setProperty, out JsonElement setEl ) || setEl.ValueKind != JsonValueKind.Object)
        {
            return 0m;
        }

        if (!setEl.TryGetProperty( "shopMoney", out JsonElement shopMoneyEl ) || shopMoneyEl.ValueKind != JsonValueKind.Object)
        {
            return 0m;
        }

        if (!shopMoneyEl.TryGetProperty( "amount", out JsonElement amountEl ) || amountEl.ValueKind != JsonValueKind.String)
        {
            return 0m;
        }

        return decimal.TryParse( amountEl.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value )
            ? value
            : 0m;
    }

    private static string ReadString( JsonElement node, string prop )
    {
        if (node.ValueKind != JsonValueKind.Object) return string.Empty;
        return node.TryGetProperty( prop, out JsonElement valueEl ) && valueEl.ValueKind == JsonValueKind.String
            ? (valueEl.GetString() ?? string.Empty)
            : string.Empty;
    }

    private static string FormatAddress( JsonElement addr )
    {
        if (addr.ValueKind != JsonValueKind.Object) return string.Empty;
        string country = ResolveCountryLabel(
            ReadString( addr, "country" ),
            ReadString( addr, "countryCodeV2" )
        );
        return string.Join( ", ", new[]
        {
            ReadString( addr, "address1" ),
            ReadString( addr, "address2" ),
            ReadString( addr, "city" ),
            ReadString( addr, "zip" ),
            country
        }.Where( x => !string.IsNullOrWhiteSpace( x ) ) );
    }

    private static string ResolveCountryLabel( string country, string countryCode )
    {
        if (!string.IsNullOrWhiteSpace( country ))
        {
            return country.Trim();
        }

        if (string.IsNullOrWhiteSpace( countryCode ))
        {
            return string.Empty;
        }

        return CountryCodeToName( countryCode ) ?? countryCode.Trim().ToUpperInvariant();
    }

    private static string? CountryCodeToName( string countryCode ) =>
        countryCode.Trim().ToUpperInvariant() switch
        {
            "PL" => "Poland",
            "AT" => "Austria",
            "DE" => "Germany",
            "NL" => "Netherlands",
            "BE" => "Belgium",
            "LT" => "Lithuania",
            "LV" => "Latvia",
            "EE" => "Estonia",
            "CZ" => "Czechia",
            "SK" => "Slovakia",
            "HU" => "Hungary",
            "RO" => "Romania",
            "BG" => "Bulgaria",
            "HR" => "Croatia",
            "SI" => "Slovenia",
            "FR" => "France",
            "IT" => "Italy",
            "ES" => "Spain",
            "PT" => "Portugal",
            "SE" => "Sweden",
            "NO" => "Norway",
            "DK" => "Denmark",
            "FI" => "Finland",
            "IE" => "Ireland",
            "GB" => "United Kingdom",
            "US" => "United States",
            "CA" => "Canada",
            "CH" => "Switzerland",
            "UA" => "Ukraine",
            "BY" => "Belarus",
            "GR" => "Greece",
            _ => null
        };

    private static string ReadCountryCode( JsonElement addr )
    {
        if (addr.ValueKind != JsonValueKind.Object) return string.Empty;
        string code = ReadString( addr, "countryCodeV2" );
        return string.IsNullOrWhiteSpace( code ) ? string.Empty : code.Trim().ToUpperInvariant();
    }

    private static decimal Round2( decimal value ) => Math.Round( value, 2, MidpointRounding.AwayFromZero );
}

internal sealed class ProductVariantKeyComparer : IEqualityComparer<(string ProductId, string VariantId)>
{
    public static ProductVariantKeyComparer Instance { get; } = new();

    public bool Equals( (string ProductId, string VariantId) x, (string ProductId, string VariantId) y ) =>
        string.Equals( x.ProductId, y.ProductId, StringComparison.OrdinalIgnoreCase ) &&
        string.Equals( x.VariantId, y.VariantId, StringComparison.OrdinalIgnoreCase );

    public int GetHashCode( (string ProductId, string VariantId) obj ) =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode( obj.ProductId ),
            StringComparer.OrdinalIgnoreCase.GetHashCode( obj.VariantId ) );
}
