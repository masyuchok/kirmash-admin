using backend.Models;
using backend.Services;
using backend.Services.Odoo;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route( "bukinistka/products" )]
public class BukinistkaProductsController : ControllerBase
{
    private readonly OdooProductService _products;
    private readonly KirmaBukinistkaOfferService _offers;

    public BukinistkaProductsController(
        OdooProductService products,
        KirmaBukinistkaOfferService offers )
    {
        _products = products;
        _offers = offers;
    }

    [HttpGet]
    public async Task<ActionResult<OdooProductListResponse>> List(
        [FromQuery] string? search,
        CancellationToken cancellationToken )
    {
        try
        {
            OdooProductListResponse response = await _products.ListProductsAsync(
                Request,
                search,
                cancellationToken );
            Dictionary<int, BukinistkaProposeEligibilityDto> eligibility =
                await _offers.GetBukProposeEligibilityMapAsync(
                    response.Products.Select( p => p.Id ),
                    cancellationToken );
            foreach (OdooProductListItem product in response.Products)
            {
                if (eligibility.TryGetValue( product.Id, out BukinistkaProposeEligibilityDto? dto ))
                {
                    product.CanProposeToKirma = dto.CanPropose;
                    product.ProposeBlockReason = dto.BlockReason;
                }
            }

            return Ok( response );
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized( new { error = ex.Message } );
        }
        catch (Exception ex)
        {
            return BadRequest( new { error = ex.Message } );
        }
    }
}
