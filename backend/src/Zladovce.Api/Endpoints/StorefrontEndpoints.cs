using Zladovce.Application.Cart;
using Zladovce.Application.Seo;

namespace Zladovce.Api.Endpoints;

/// <summary>Public endpoints of the storefront besides the catalog: cart quote and SEO routes (no authentication).</summary>
public static class StorefrontEndpoints
{
    public static void MapStorefrontEndpoints(this IEndpointRouteBuilder api)
    {
        var cart = api.MapGroup("/cart").WithTags("Cart").AllowAnonymous();

        cart.MapPost("/quote", async (CartQuoteRequest request, ICartPricingService service, CancellationToken ct) =>
                TypedResults.Ok(await service.QuoteAsync(request.ToItems(), ct)))
            .WithName("QuoteCart")
            .WithSummary("Prices the browser cart from the database: lines, adjustments, subtotal, shipping, minimum order, total.")
            .WithDescription(
                "The client sends variant ids and quantities only; every price comes from the database. Lines with the same variant " +
                "are merged. A variant that cannot be bought (unknown, inactive, archived, hidden product or category, out of stock) " +
                "is left out of `lines` and reported as `unavailable` in `adjustments`; a quantity above `maxOrderQuantity` is reduced " +
                "and reported as `quantityReduced`; with the optional `knownUnitPrice` a different current price is reported as " +
                "`priceChanged`. Shipping is charged unless the subtotal is at or above the free-shipping threshold; the minimum " +
                "order compares the subtotal. `canCheckout` is false when shipping is disabled, there are no lines or the minimum " +
                "is not met. The exact stock is never returned. " +
                "Errors: 400 VALIDATION_FAILED (`items` empty or more than 50, `items[i].quantity` not 1–99, `items[i].variantId` " +
                "missing or empty, `items[i].knownUnitPrice` invalid, a required member missing); 400 BAD_REQUEST (malformed JSON, " +
                "a value of the wrong type such as a malformed uuid).")
            .Problems(StatusCodes.Status400BadRequest);

        api.MapGet("/seo/routes", async (ISeoRoutesService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetRoutesAsync(ct)))
            .WithTags("SEO")
            .AllowAnonymous()
            .WithName("GetSeoRoutes")
            .WithSummary("Slugs and last-modified times of the visible categories and products, for prerendering and the sitemap.")
            .WithDescription("Same visibility as the public catalog. Blog posts are added in a later phase as a new member.");
    }
}
