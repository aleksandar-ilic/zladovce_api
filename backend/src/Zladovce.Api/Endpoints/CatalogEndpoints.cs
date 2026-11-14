using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Zladovce.Api.Errors;
using Zladovce.Application.Catalog;
using Zladovce.Application.Categories;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Inventory;
using Zladovce.Application.Media;

namespace Zladovce.Api.Endpoints;

internal static class EndpointDocs
{
    /// <summary>Documents Problem Details responses for the given status codes.</summary>
    public static RouteHandlerBuilder Problems(this RouteHandlerBuilder builder, params int[] statusCodes)
    {
        foreach (var statusCode in statusCodes)
        {
            builder.Produces<ApiProblem>(statusCode, ApiProblem.ContentType);
        }

        return builder;
    }
}

/// <summary>Public catalog (no authentication).</summary>
public static class PublicCatalogEndpoints
{
    public const int MaxAvailabilityIds = 100;
    public const int DefaultRelatedLimit = 4;
    public const int MaxRelatedLimit = 12;

    public static void MapPublicCatalogEndpoints(this IEndpointRouteBuilder api)
    {
        var catalog = api.MapGroup("/catalog").WithTags("Catalog").AllowAnonymous();

        catalog.MapGet("/categories", async (IPublicCatalogService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetCategoriesAsync(ct)))
            .WithName("GetCatalogCategories")
            .WithSummary("Active categories in display order, with the number of visible products.");

        catalog.MapGet("/categories/{slug}", async (string slug, IPublicCatalogService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetCategoryAsync(slug, ct)))
            .WithName("GetCatalogCategory")
            .WithSummary("An active category with its SEO data.")
            .WithDescription("Errors: 404 NOT_FOUND (unknown, inactive or archived).")
            .Problems(StatusCodes.Status404NotFound);

        catalog.MapGet("/products", async ([AsParameters] PublicProductListQuery query, IPublicCatalogService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetProductsAsync(query.ToCriteria(), ct)))
            .WithName("GetCatalogProducts")
            .WithSummary("Product grid: visible products with the listing price (lowest variant price) and availability.")
            .WithDescription(
                "Visible = active, not archived, in an active category, with at least one active variant. " +
                "Sort `bestselling` is accepted and ordered like `newest` until order data exists. Errors: 400 VALIDATION_FAILED.")
            .Problems(StatusCodes.Status400BadRequest);

        catalog.MapGet("/products/{slug}", async (string slug, IPublicCatalogService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetProductAsync(slug, ct)))
            .WithName("GetCatalogProduct")
            .WithSummary("Product page: variants with prices and availability (never the exact stock), images, SEO.")
            .WithDescription("Errors: 404 NOT_FOUND (unknown or not visible).")
            .Problems(StatusCodes.Status404NotFound);

        catalog.MapGet("/products/{slug}/related", async (
                string slug,
                [FromQuery(Name = "limit")] int? limit,
                IPublicCatalogService service,
                CancellationToken ct) =>
            {
                var take = limit ?? DefaultRelatedLimit;
                if (take is < 1 or > MaxRelatedLimit)
                {
                    throw new RequestValidationException("limit", $"Broj proizvoda mora biti između 1 i {MaxRelatedLimit}.");
                }

                return TypedResults.Ok(await service.GetRelatedProductsAsync(slug, take, ct));
            })
            .WithName("GetCatalogRelatedProducts")
            .WithSummary("Other visible products of the same category (featured first, then newest). `limit` 1–12, default 4.")
            .WithDescription("Errors: 400 VALIDATION_FAILED; 404 NOT_FOUND.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);

        catalog.MapGet("/availability", async (
                [FromQuery(Name = "variantIds")] string[]? variantIds,
                IPublicCatalogService service,
                CancellationToken ct) =>
                TypedResults.Ok(await service.GetAvailabilityAsync(ParseVariantIds(variantIds), ct)))
            .WithName("GetCatalogAvailability")
            .WithSummary("Live price and availability of variants (for prerendered pages and the cart).")
            .WithDescription(
                "`variantIds`: up to 100 ids, repeated (`variantIds=a&variantIds=b`) or comma-separated (`variantIds=a,b`). " +
                "Variants that are not on sale are missing from the result. Errors: 400 VALIDATION_FAILED.")
            .Problems(StatusCodes.Status400BadRequest);
    }

    internal static IReadOnlyCollection<Guid> ParseVariantIds(string[]? values)
    {
        var ids = new HashSet<Guid>();
        foreach (var part in (values ?? []).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (!Guid.TryParse(part, out var id))
            {
                throw new RequestValidationException("variantIds", "Identifikator varijante nije ispravan.");
            }

            ids.Add(id);
        }

        if (ids.Count == 0)
        {
            throw new RequestValidationException("variantIds", "Navedite bar jednu varijantu.");
        }

        if (ids.Count > MaxAvailabilityIds)
        {
            throw new RequestValidationException("variantIds", $"Najviše {MaxAvailabilityIds} varijanti po zahtevu.");
        }

        return ids;
    }
}

/// <summary>Administration of categories, products, variants, images and media uploads.</summary>
public static class AdminCatalogEndpoints
{
    public static void MapAdminCatalogEndpoints(this IEndpointRouteBuilder admin)
    {
        MapCategories(admin.MapGroup("/categories").WithTags("Admin – categories"));
        MapProducts(admin.MapGroup("/products").WithTags("Admin – products"));

        admin.MapPost("/media/upload-url", async (CreateUploadUrlRequest request, IMediaUploadService service, CancellationToken ct) =>
                TypedResults.Ok(await service.CreateUploadUrlAsync(request, ct)))
            .WithTags("Admin – media")
            .WithName("CreateMediaUploadUrl")
            .WithSummary("Pre-signed browser upload (multipart POST straight to the storage) for a product or category image.")
            .WithDescription("Errors: 400 VALIDATION_FAILED (content type, size, purpose).")
            .Problems(StatusCodes.Status400BadRequest);
    }

    private static void MapCategories(RouteGroupBuilder categories)
    {
        categories.MapGet("", async (ICategoryService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListAsync(ct)))
            .WithName("ListAdminCategories")
            .WithSummary("All categories that are not archived, with product counts.");

        categories.MapPost("", async Task<Created<AdminCategoryResponse>> (SaveCategoryRequest request, ICategoryService service, CancellationToken ct) =>
            {
                var created = await service.CreateAsync(request, ct);
                return TypedResults.Created($"{EndpointMapping.ApiBasePath}/admin/categories/{created.Id}", created);
            })
            .WithName("CreateAdminCategory")
            .WithSummary("Create a category. The slug is generated from the name when not given.")
            .WithDescription(
                "Errors: 400 VALIDATION_FAILED, INVALID_IMAGE, UPLOAD_NOT_FOUND; 409 SLUG_TAKEN (`errors.slug`), IMAGE_NOT_READY, " +
                "IMAGE_ALREADY_ATTACHED.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict);

        categories.MapPut("/order", async (ReorderCategoriesRequest request, ICategoryService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ReorderAsync(request, ct)))
            .WithName("ReorderAdminCategories")
            .WithSummary("Set the display order of all categories.")
            .WithDescription("Errors: 400 VALIDATION_FAILED (must list every non-archived category exactly once).")
            .Problems(StatusCodes.Status400BadRequest);

        categories.MapGet("/{id:guid}", async (Guid id, ICategoryService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetAsync(id, ct)))
            .WithName("GetAdminCategory")
            .WithSummary("A category.")
            .WithDescription("Errors: 404 NOT_FOUND (also archived).")
            .Problems(StatusCodes.Status404NotFound);

        categories.MapPut("/{id:guid}", async (Guid id, SaveCategoryRequest request, ICategoryService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateAsync(id, request, ct)))
            .WithName("UpdateAdminCategory")
            .WithSummary("Update a category. Empty slug keeps the current one; `imageKey` null removes the image.")
            .WithDescription(
                "Errors: 400 VALIDATION_FAILED, INVALID_IMAGE, UPLOAD_NOT_FOUND; 404 NOT_FOUND; 409 SLUG_TAKEN (`errors.slug`), " +
                "IMAGE_NOT_READY, IMAGE_ALREADY_ATTACHED.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        categories.MapDelete("/{id:guid}", async Task<NoContent> (Guid id, ICategoryService service, CancellationToken ct) =>
            {
                await service.ArchiveAsync(id, ct);
                return TypedResults.NoContent();
            })
            .WithName("ArchiveAdminCategory")
            .WithSummary("Archive a category. Refused while it has products that are not archived.")
            .WithDescription(
                "The category image is removed with it: `image` is cleared in the same save and the image files are deleted " +
                "afterwards. Errors: 404 NOT_FOUND; 409 CATEGORY_HAS_PRODUCTS.")
            .Problems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
    }

    private static void MapProducts(RouteGroupBuilder products)
    {
        products.MapGet("", async ([AsParameters] AdminProductListQuery query, IAdminProductService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListAsync(query.ToCriteria(), ct)))
            .WithName("ListAdminProducts")
            .WithSummary("Products with price range, stock summary and main image.")
            .WithDescription("Errors: 400 VALIDATION_FAILED.")
            .Problems(StatusCodes.Status400BadRequest);

        products.MapPost("", async Task<Created<AdminProductResponse>> (CreateProductRequest request, IAdminProductService service, CancellationToken ct) =>
            {
                var created = await service.CreateAsync(request, ct);
                return TypedResults.Created($"{EndpointMapping.ApiBasePath}/admin/products/{created.Id}", created);
            })
            .WithName("CreateAdminProduct")
            .WithSummary("Create a product with at least one variant; each variant gets its stock record (and an InitialStock movement when above 0).")
            .WithDescription(
                "Errors: 400 VALIDATION_FAILED (also unknown/archived categoryId); 409 SLUG_TAKEN (`errors.slug`), SKU_TAKEN " +
                "(`errors.sku` for the product SKU, otherwise one `errors[\"variants[i].sku\"]` per taken variant SKU, i = index in the request).")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict);

        products.MapGet("/{id:guid}", async (Guid id, IAdminProductService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetAsync(id, ct)))
            .WithName("GetAdminProduct")
            .WithSummary("A product (archived ones too) with its variants, stock and images.")
            .WithDescription("Errors: 404 NOT_FOUND.")
            .Problems(StatusCodes.Status404NotFound);

        products.MapPut("/{id:guid}", async (Guid id, UpdateProductRequest request, IAdminProductService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateAsync(id, request, ct)))
            .WithName("UpdateAdminProduct")
            .WithSummary("Update product values. Requires the `version` last read; a stale version gives 409 CONCURRENT_EDIT.")
            .WithDescription(
                "Replaces every value: all members of the body must be sent. Errors: 400 VALIDATION_FAILED; 404 NOT_FOUND; " +
                "409 CONCURRENT_EDIT, SLUG_TAKEN (`errors.slug`), SKU_TAKEN (`errors.sku`), NO_ACTIVE_VARIANT, PRODUCT_ARCHIVED.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        products.MapPost("/{id:guid}/archive", async (Guid id, IAdminProductService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ArchiveAsync(id, ct)))
            .WithName("ArchiveAdminProduct")
            .WithSummary("Archive a product (hidden everywhere, slug and SKU become reusable). Idempotent.")
            .WithDescription("Errors: 404 NOT_FOUND.")
            .Problems(StatusCodes.Status404NotFound);

        products.MapPost("/{id:guid}/restore", async (Guid id, IAdminProductService service, CancellationToken ct) =>
                TypedResults.Ok(await service.RestoreAsync(id, ct)))
            .WithName("RestoreAdminProduct")
            .WithSummary("Restore an archived product. Idempotent.")
            .WithDescription("Errors: 404 NOT_FOUND; 409 SLUG_TAKEN (`errors.slug`), SKU_TAKEN (`errors.sku`) (reused meanwhile), CATEGORY_ARCHIVED.")
            .Problems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        products.MapPost("/{id:guid}/variants", async Task<Created<AdminProductResponse>> (
                Guid id, CreateVariantRequest request, IAdminProductService service, CancellationToken ct) =>
            {
                var product = await service.AddVariantAsync(id, request, ct);
                return TypedResults.Created($"{EndpointMapping.ApiBasePath}/admin/products/{id}", product);
            })
            .WithName("AddAdminProductVariant")
            .WithSummary("Add a variant with its opening stock. Returns the whole product.")
            .WithDescription("Errors: 400 VALIDATION_FAILED; 404 NOT_FOUND; 409 SKU_TAKEN (`errors.sku`), PRODUCT_ARCHIVED, CONFLICT (50 variants).")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        products.MapPut("/{id:guid}/variants/{variantId:guid}", async (
                Guid id, Guid variantId, VariantRequest request, IAdminProductService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateVariantAsync(id, variantId, request, ct)))
            .WithName("UpdateAdminProductVariant")
            .WithSummary("Update a variant (stock is changed through the inventory endpoints). Returns the whole product.")
            .WithDescription("Errors: 400 VALIDATION_FAILED; 404 NOT_FOUND; 409 SKU_TAKEN (`errors.sku`), LAST_ACTIVE_VARIANT, PRODUCT_ARCHIVED.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        products.MapDelete("/{id:guid}/variants/{variantId:guid}", async (
                Guid id, Guid variantId, IAdminProductService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ArchiveVariantAsync(id, variantId, ct)))
            .WithName("ArchiveAdminProductVariant")
            .WithSummary("Archive a variant. Returns the whole product.")
            .WithDescription("Errors: 404 NOT_FOUND; 409 LAST_VARIANT, LAST_ACTIVE_VARIANT, PRODUCT_ARCHIVED.")
            .Problems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        products.MapPost("/{id:guid}/images", async Task<Created<AdminProductResponse>> (
                Guid id, AttachProductImageRequest request, IAdminProductService service, CancellationToken ct) =>
            {
                var product = await service.AttachImageAsync(id, request, ct);
                return TypedResults.Created($"{EndpointMapping.ApiBasePath}/admin/products/{id}", product);
            })
            .WithName("AttachAdminProductImage")
            .WithSummary("Attach an uploaded image (key from upload-url). The first image becomes the main image. Returns the whole product.")
            .WithDescription(
                "Errors: 400 VALIDATION_FAILED (key not a product upload key), INVALID_IMAGE, UPLOAD_NOT_FOUND; 404 NOT_FOUND; " +
                "409 IMAGE_NOT_READY (processor still running, retry), IMAGE_ALREADY_ATTACHED, IMAGE_LIMIT_REACHED (20), PRODUCT_ARCHIVED.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        products.MapPut("/{id:guid}/images/order", async (
                Guid id, ReorderProductImagesRequest request, IAdminProductService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ReorderImagesAsync(id, request, ct)))
            .WithName("ReorderAdminProductImages")
            .WithSummary("Set the order of all images and the main image (null = first). Returns the whole product.")
            .WithDescription("Errors: 400 VALIDATION_FAILED; 404 NOT_FOUND; 409 PRODUCT_ARCHIVED.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        products.MapPut("/{id:guid}/images/{imageId:guid}", async (
                Guid id, Guid imageId, UpdateProductImageRequest request, IAdminProductService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateImageAsync(id, imageId, request, ct)))
            .WithName("UpdateAdminProductImage")
            .WithSummary("Change the alt text of an image. Returns the whole product.")
            .WithDescription("Errors: 400 VALIDATION_FAILED; 404 NOT_FOUND; 409 PRODUCT_ARCHIVED.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        products.MapDelete("/{id:guid}/images/{imageId:guid}", async (
                Guid id, Guid imageId, IAdminProductService service, CancellationToken ct) =>
                TypedResults.Ok(await service.DeleteImageAsync(id, imageId, ct)))
            .WithName("DeleteAdminProductImage")
            .WithSummary("Remove an image and its files; the next image becomes main when needed. Returns the whole product.")
            .WithDescription("Errors: 404 NOT_FOUND; 409 PRODUCT_ARCHIVED.")
            .Problems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
    }
}

/// <summary>Administration of stock.</summary>
public static class AdminInventoryEndpoints
{
    public const int DefaultLowStockLimit = 10;
    public const int MaxLowStockLimit = 100;

    public static void MapAdminInventoryEndpoints(this IEndpointRouteBuilder admin)
    {
        var inventory = admin.MapGroup("/inventory").WithTags("Admin – inventory");

        inventory.MapGet("", async ([AsParameters] InventoryListQuery query, IInventoryService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListAsync(query.ToCriteria(), ct)))
            .WithName("ListInventory")
            .WithSummary("Variants (of products that are not archived) with stock, threshold and status.")
            .WithDescription(
                "`status`: `ok`, `low`, `out`; several values comma-separated (`status=low,out`) and/or repeated " +
                "(`status=low&status=out`) match any of them. `status=low,out` returns exactly the rows counted by " +
                "`GET /admin/dashboard/low-stock` (link the dashboard card there). Errors: 400 VALIDATION_FAILED.")
            .Problems(StatusCodes.Status400BadRequest);

        inventory.MapPost("/{variantId:guid}/adjustments", async (
                Guid variantId, AdjustStockRequest request, IInventoryService service, CancellationToken ct) =>
                TypedResults.Ok(await service.AdjustAsync(variantId, request, ct)))
            .WithName("AdjustInventory")
            .WithSummary("Manual stock correction with a mandatory reason. Atomic; stock never goes below zero.")
            .WithDescription("Errors: 400 VALIDATION_FAILED; 404 NOT_FOUND; 409 INSUFFICIENT_STOCK.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        inventory.MapPut("/{variantId:guid}/threshold", async (
                Guid variantId, SetLowStockThresholdRequest request, IInventoryService service, CancellationToken ct) =>
                TypedResults.Ok(await service.SetLowStockThresholdAsync(variantId, request, ct)))
            .WithName("SetInventoryThreshold")
            .WithSummary("Set the low-stock threshold of a variant.")
            .WithDescription("Errors: 400 VALIDATION_FAILED; 404 NOT_FOUND.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);

        inventory.MapGet("/{variantId:guid}/movements", async (
                Guid variantId, [AsParameters] PageQuery query, IInventoryService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetMovementsAsync(variantId, Application.Common.PageRequest.From(query.Page, query.PageSize), ct)))
            .WithName("ListInventoryMovements")
            .WithSummary("Stock movements of a variant, newest first, with the administrator's name.")
            .WithDescription("Errors: 400 VALIDATION_FAILED; 404 NOT_FOUND.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);

        admin.MapGet("/dashboard/low-stock", async ([FromQuery(Name = "limit")] int? limit, IInventoryService service, CancellationToken ct) =>
            {
                var take = limit ?? DefaultLowStockLimit;
                if (take is < 1 or > MaxLowStockLimit)
                {
                    throw new RequestValidationException("limit", $"Broj stavki mora biti između 1 i {MaxLowStockLimit}.");
                }

                return TypedResults.Ok(await service.GetLowStockAsync(take, ct));
            })
            .WithTags("Admin – dashboard")
            .WithName("GetDashboardLowStock")
            .WithSummary("Variants with status `low` or `out` (at or below their low-stock threshold), emptiest first. `limit` 1–100, default 10.")
            .WithDescription(
                "Same rows as `GET /admin/inventory?status=low,out` (`totalItems` matches); the dashboard card should link there. " +
                "Errors: 400 VALIDATION_FAILED.")
            .Problems(StatusCodes.Status400BadRequest);
    }
}
