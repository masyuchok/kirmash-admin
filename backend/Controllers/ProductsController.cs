using backend.Models;
using backend.Services;
using backend.Services.Shopify;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers
{
    [ApiController]
    [Route( "[controller]" )]
    [Authorize]
    public class ProductsController : Controller
    {
        private readonly ProductService _service;
        private readonly VatReportUnpaidLinkService _unpaidLink;

        public ProductsController( ProductService service, VatReportUnpaidLinkService unpaidLink )
        {
            _service = service;
            _unpaidLink = unpaidLink;
        }

        [HttpGet]
        public async Task<ActionResult<List<ProductWithSuppliersListItem>>> GetAll()
        {
            try
            {
                List<ProductWithSuppliersListItem> products = await _service.GetProductsWithSuppliersAsync();
                try
                {
                    List<ProductOverpaidLineItem> overpaidLines = await _unpaidLink.GetAllOverpaidLinesAsync();
                    Dictionary<string, List<ProductOverpaidLineItem>> overpaidByProductId = overpaidLines
                        .GroupBy( line => ShopifyIds.NormalizeProductId( line.ShopifyProductId ) )
                        .ToDictionary(
                            g => g.Key,
                            g => g.ToList(),
                            StringComparer.OrdinalIgnoreCase );

                    foreach (ProductWithSuppliersListItem product in products)
                    {
                        if (overpaidByProductId.TryGetValue( product.ShopifyProductId, out List<ProductOverpaidLineItem>? lines ))
                        {
                            product.OverpaidLines = lines;
                        }
                    }
                }
                catch
                {
                    // Overpaid badges are optional; catalog still loads.
                }

                return Ok( products );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка атрымання прадуктаў", details = ex.Message } );
            }
        }

        /// <summary>
        /// Cursor page of Shopify catalog with optional title search / product type filter.
        /// Search runs on Shopify (whole catalog), not only the current page.
        /// </summary>
        [HttpGet( "catalog" )]
        public async Task<ActionResult<ProductCatalogPageResponse>> GetCatalogPage(
            [FromQuery] string? search = null,
            [FromQuery] string? types = null,
            [FromQuery] string? after = null,
            [FromQuery] int pageSize = 50,
            [FromQuery] bool includeTypes = false )
        {
            try
            {
                List<string> typeList = string.IsNullOrWhiteSpace( types )
                    ? []
                    : types.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
                        .Where( t => t.Length > 0 )
                        .Distinct( StringComparer.OrdinalIgnoreCase )
                        .ToList();

                ProductCatalogPageResponse page = await _service.GetProductsCatalogPageAsync(
                    search,
                    typeList,
                    after,
                    pageSize,
                    includeTypes );

                try
                {
                    HashSet<string> pageIds = page.Items
                        .Select( p => ShopifyIds.NormalizeProductId( p.ShopifyProductId ) )
                        .ToHashSet( StringComparer.OrdinalIgnoreCase );
                    if (pageIds.Count > 0)
                    {
                        List<ProductOverpaidLineItem> overpaidLines = await _unpaidLink.GetAllOverpaidLinesAsync();
                        Dictionary<string, List<ProductOverpaidLineItem>> overpaidByProductId = overpaidLines
                            .Where( line => pageIds.Contains(
                                ShopifyIds.NormalizeProductId( line.ShopifyProductId ) ) )
                            .GroupBy( line => ShopifyIds.NormalizeProductId( line.ShopifyProductId ) )
                            .ToDictionary(
                                g => g.Key,
                                g => g.ToList(),
                                StringComparer.OrdinalIgnoreCase );

                        foreach (ProductWithSuppliersListItem product in page.Items)
                        {
                            if (overpaidByProductId.TryGetValue(
                                    product.ShopifyProductId,
                                    out List<ProductOverpaidLineItem>? lines ))
                            {
                                product.OverpaidLines = lines;
                            }
                        }
                    }
                }
                catch
                {
                    // Overpaid badges are optional.
                }

                return Ok( page );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка атрымання прадуктаў", details = ex.Message } );
            }
        }

        [HttpGet( "{shopifyProductId}/history" )]
        public async Task<ActionResult<ProductHistoryResponse>> GetHistory(
            string shopifyProductId,
            [FromQuery] string? shopifyVariantId = null,
            [FromQuery] int? supplierId = null,
            [FromQuery] string? variantTitle = null )
        {
            try
            {
                ProductHistoryResponse history = await _service.GetProductHistoryAsync(
                    shopifyProductId,
                    shopifyVariantId,
                    supplierId,
                    variantTitle
                );
                return Ok( history );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка атрымання гісторыі прадукту", details = ex.Message } );
            }
        }

        [HttpPost( "sync-unsynced" )]
        public async Task<ActionResult<ProductSyncResult>> SyncUnsynced( [FromBody] ProductSyncRequest request )
        {
            try
            {
                ProductSyncResult result = await _service.SyncUnsyncedSupplierRowAsync(
                    request.ShopifyProductId,
                    request.SupplierId
                );
                return Ok( result );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest( new { error = ex.Message } );
            }
            catch (Exception ex)
            {
                return StatusCode( 500, new { error = "Памылка сінхранізацыі з Shopify", details = ex.Message } );
            }
        }
    }
}
