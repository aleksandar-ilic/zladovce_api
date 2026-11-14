using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Common.Paging;
using Zladovce.Application.Inventory;
using Zladovce.Application.Media;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.Inventory;

namespace Zladovce.Application.Catalog;

public interface IAdminProductService
{
    Task<PagedResult<AdminProductListItem>> ListAsync(AdminProductListCriteria criteria, CancellationToken cancellationToken);
    Task<AdminProductResponse> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<AdminProductResponse> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken);
    Task<AdminProductResponse> UpdateAsync(Guid id, UpdateProductRequest request, CancellationToken cancellationToken);
    Task<AdminProductResponse> ArchiveAsync(Guid id, CancellationToken cancellationToken);
    Task<AdminProductResponse> RestoreAsync(Guid id, CancellationToken cancellationToken);
    Task<AdminProductResponse> AddVariantAsync(Guid id, CreateVariantRequest request, CancellationToken cancellationToken);
    Task<AdminProductResponse> UpdateVariantAsync(Guid id, Guid variantId, VariantRequest request, CancellationToken cancellationToken);
    Task<AdminProductResponse> ArchiveVariantAsync(Guid id, Guid variantId, CancellationToken cancellationToken);
    Task<AdminProductResponse> AttachImageAsync(Guid id, AttachProductImageRequest request, CancellationToken cancellationToken);
    Task<AdminProductResponse> ReorderImagesAsync(Guid id, ReorderProductImagesRequest request, CancellationToken cancellationToken);
    Task<AdminProductResponse> UpdateImageAsync(Guid id, Guid imageId, UpdateProductImageRequest request, CancellationToken cancellationToken);
    Task<AdminProductResponse> DeleteImageAsync(Guid id, Guid imageId, CancellationToken cancellationToken);
}

public sealed class AdminProductService(
    IZladovceDbContext db,
    IInventoryService inventory,
    IImageProcessor images,
    IHtmlContentSanitizer sanitizer,
    IOptions<MediaOptions> mediaOptions,
    TimeProvider timeProvider) : IAdminProductService
{
    private readonly MediaOptions _media = mediaOptions.Value;

    public async Task<PagedResult<AdminProductListItem>> ListAsync(AdminProductListCriteria criteria, CancellationToken cancellationToken)
    {
        var query = db.Products.AsNoTracking();

        query = criteria.Archived ? query.Where(p => p.DeletedAt != null) : query.Where(p => p.DeletedAt == null);

        if (criteria.IsActive is { } isActive)
        {
            query = query.Where(p => p.IsActive == isActive);
        }

        if (criteria.CategoryId is { } categoryId)
        {
            query = query.Where(p => p.CategoryId == categoryId);
        }

        if (DbSearch.ContainsPattern(criteria.Search) is { } pattern)
        {
            query = query.Where(p =>
                EF.Functions.Like(DbSearch.Unaccent(p.Name).ToLower(), DbSearch.Unaccent(pattern).ToLower())
                || EF.Functions.Like(DbSearch.Unaccent(p.Sku).ToLower(), DbSearch.Unaccent(pattern).ToLower())
                || p.Variants.Any(v => v.DeletedAt == null
                    && EF.Functions.Like(DbSearch.Unaccent(v.Sku).ToLower(), DbSearch.Unaccent(pattern).ToLower())));
        }

        var total = await query.CountAsync(cancellationToken);

        var ordered = criteria.Sort switch
        {
            AdminProductSort.Oldest => query.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id),
            AdminProductSort.Name => query.OrderBy(p => p.Name).ThenBy(p => p.Id),
            AdminProductSort.NameDescending => query.OrderByDescending(p => p.Name).ThenBy(p => p.Id),
            AdminProductSort.Updated => query.OrderByDescending(p => p.UpdatedAt).ThenBy(p => p.Id),
            _ => query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id),
        };

        var products = await ordered
            .Skip(criteria.Page.Skip).Take(criteria.Page.PageSize)
            .Include(p => p.Variants)
            .Include(p => p.Images)
            .ToListAsync(cancellationToken);

        var categoryIds = products.Select(p => p.CategoryId).Distinct().ToList();
        var categories = await db.Categories.AsNoTracking()
            .Where(c => categoryIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        var stock = await inventory.GetStockAsync(
            [.. products.SelectMany(p => p.CurrentVariants).Select(v => v.Id)], cancellationToken);

        var items = products.Select(product =>
        {
            var variants = product.CurrentVariants.ToList();
            var levels = variants.Select(v => stock.GetValueOrDefault(v.Id) ?? new StockLevel(v.Id, 0, 0)).ToList();
            var price = ListingPrice.From(variants.Select(v => (v.RegularPrice, v.DiscountPrice)));
            var status = levels.Count == 0 || levels.All(l => l.Status == StockStatus.Out) ? StockStatus.Out
                : levels.Any(l => l.Status != StockStatus.Ok) ? StockStatus.Low
                : StockStatus.Ok;

            return new AdminProductListItem(
                product.Id,
                product.Name,
                product.Slug,
                product.Sku,
                new AdminProductCategory(product.CategoryId, categories.GetValueOrDefault(product.CategoryId) ?? string.Empty),
                product.IsActive,
                product.IsFeatured,
                product.IsArchived,
                variants.Count,
                price?.Price,
                price?.HasPriceRange ?? false,
                levels.Sum(l => l.QuantityOnHand),
                status,
                product.MainImage is { } main ? ProductImageResponse.From(_media, main) : null,
                product.CreatedAt,
                product.UpdatedAt);
        }).ToList();

        return PagedResult<AdminProductListItem>.Create(items, criteria.Page.Page, criteria.Page.PageSize, total);
    }

    public async Task<AdminProductResponse> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var product = await db.Products
            .AsNoTracking()
            .Include(p => p.Variants)
            .Include(p => p.Images)
            .SingleOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException();

        var categoryName = await db.Categories.AsNoTracking()
            .Where(c => c.Id == product.CategoryId)
            .Select(c => c.Name)
            .SingleAsync(cancellationToken);

        var variants = product.CurrentVariants.ToList();
        var stock = await inventory.GetStockAsync([.. variants.Select(v => v.Id)], cancellationToken);

        return new AdminProductResponse(
            product.Id,
            new AdminProductCategory(product.CategoryId, categoryName),
            product.Name,
            product.Slug,
            product.ShortDescription,
            product.Description,
            product.Sku,
            product.IsActive,
            product.IsFeatured,
            product.IsArchived,
            product.SeoTitle,
            product.SeoDescription,
            [.. variants.Select(v =>
            {
                var level = stock.GetValueOrDefault(v.Id) ?? new StockLevel(v.Id, 0, 0);
                return new AdminVariantResponse(
                    v.Id, v.Name, v.Sku, v.UnitAmount, v.MeasurementUnit, v.RegularPrice, v.DiscountPrice, v.EffectivePrice,
                    v.IsActive, v.SortOrder, level.QuantityOnHand, level.LowStockThreshold, level.Status, v.CreatedAt, v.UpdatedAt);
            })],
            [.. product.OrderedImages.Select(i => ProductImageResponse.From(_media, i))],
            product.Version,
            product.CreatedAt,
            product.UpdatedAt,
            product.DeletedAt);
    }

    public async Task<AdminProductResponse> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var categoryId = request.CategoryId!.Value;

        await EnsureCategoryUsableAsync(categoryId, cancellationToken);
        var slug = await SlugAllocator.AllocateAsync(request.Slug, request.Name!, candidate => SlugTakenAsync(candidate, null, cancellationToken));
        await EnsureProductSkuFreeAsync(request.Sku, null, cancellationToken);
        await EnsureVariantSkusFreeAsync(VariantSkuFields(request), null, cancellationToken);

        var product = Product.Create(ToValues(request, categoryId, slug), [.. request.Variants!.Select((v, index) => v.ToValues(index))], now);
        db.Products.Add(product);

        for (var index = 0; index < request.Variants!.Count; index++)
        {
            inventory.RegisterVariant(product.Variants[index].Id, request.Variants[index].InitialStock, request.Variants[index].LowStockThreshold);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (!cancellationToken.IsCancellationRequested)
        {
            // A unique index refused the insert after the checks above passed (another administrator saved the same slug
            // or SKU at that moment). Repeating the checks reports the exact field, including the variant index.
            if (await SlugTakenAsync(slug, null, cancellationToken))
            {
                throw SlugAllocator.SlugTaken();
            }

            await EnsureProductSkuFreeAsync(request.Sku, null, cancellationToken);
            await EnsureVariantSkusFreeAsync(VariantSkuFields(request), null, cancellationToken);
            throw;
        }

        return await GetAsync(product.Id, cancellationToken);
    }

    public async Task<AdminProductResponse> UpdateAsync(Guid id, UpdateProductRequest request, CancellationToken cancellationToken)
    {
        var product = await LoadForChangeAsync(id, cancellationToken);
        var categoryId = request.CategoryId!.Value;

        if (product.Version != request.Version!.Value)
        {
            throw new ConflictException(ErrorTexts.ConcurrentEditDetail, ErrorCodes.ConcurrentEdit, ErrorTexts.ConcurrentEditTitle);
        }

        if (categoryId != product.CategoryId)
        {
            await EnsureCategoryUsableAsync(categoryId, cancellationToken);
        }

        var slug = product.Slug;
        if (!string.IsNullOrWhiteSpace(request.Slug) && request.Slug != product.Slug)
        {
            slug = await SlugAllocator.AllocateAsync(request.Slug, request.Name!, candidate => SlugTakenAsync(candidate, id, cancellationToken));
        }

        if (!string.Equals(request.Sku!.Trim(), product.Sku, StringComparison.Ordinal))
        {
            await EnsureProductSkuFreeAsync(request.Sku, id, cancellationToken);
        }

        if (request.IsActive && !product.Variants.Any(v => v.IsSellable))
        {
            throw new ConflictException(ErrorTexts.NoActiveVariantDetail, ErrorCodes.NoActiveVariant, ErrorTexts.NoActiveVariantTitle);
        }

        product.Update(ToValues(request, categoryId, slug), timeProvider.GetUtcNow());

        // The UPDATE is conditional on the version the administrator saw; a concurrent change gives 409 CONCURRENT_EDIT.
        db.Entry(product).Property(p => p.Version).OriginalValue = request.Version.Value;

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<AdminProductResponse> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var product = await LoadAsync(id, cancellationToken);
        product.Archive(timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<AdminProductResponse> RestoreAsync(Guid id, CancellationToken cancellationToken)
    {
        var product = await LoadAsync(id, cancellationToken);
        if (product.IsArchived)
        {
            // The slug and the SKU were free to reuse while the product was archived.
            if (await SlugTakenAsync(product.Slug, id, cancellationToken))
            {
                throw SlugAllocator.SlugTaken();
            }

            await EnsureProductSkuFreeAsync(product.Sku, id, cancellationToken);

            if (!await db.Categories.AnyAsync(c => c.Id == product.CategoryId && c.DeletedAt == null, cancellationToken))
            {
                throw new ConflictException(ErrorTexts.CategoryArchivedDetail, ErrorCodes.CategoryArchived, ErrorTexts.CategoryArchivedTitle);
            }

            product.Restore(timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(id, cancellationToken);
    }

    public async Task<AdminProductResponse> AddVariantAsync(Guid id, CreateVariantRequest request, CancellationToken cancellationToken)
    {
        var product = await LoadForChangeAsync(id, cancellationToken);
        if (product.CurrentVariants.Count() >= Product.MaxVariants)
        {
            throw new ConflictException($"Proizvod može imati najviše {Product.MaxVariants} varijanti.");
        }

        await EnsureVariantSkusFreeAsync([(request.Sku, "sku")], null, cancellationToken);

        var nextPosition = product.CurrentVariants.Select(v => v.SortOrder + 1).DefaultIfEmpty(0).Max();
        var variant = product.AddVariant(request.ToValues(nextPosition), timeProvider.GetUtcNow());
        db.ProductVariants.Add(variant);
        inventory.RegisterVariant(variant.Id, request.InitialStock, request.LowStockThreshold);

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<AdminProductResponse> UpdateVariantAsync(Guid id, Guid variantId, VariantRequest request, CancellationToken cancellationToken)
    {
        var product = await LoadForChangeAsync(id, cancellationToken);
        var variant = FindVariant(product, variantId);

        if (!string.Equals(request.Sku!.Trim(), variant.Sku, StringComparison.Ordinal))
        {
            await EnsureVariantSkusFreeAsync([(request.Sku, "sku")], variantId, cancellationToken);
        }

        if (!request.IsActive)
        {
            EnsureAllowed(product.CheckVariantDeactivation(variantId));
        }

        product.UpdateVariant(variantId, request.ToValues(variant.SortOrder), timeProvider.GetUtcNow());

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<AdminProductResponse> ArchiveVariantAsync(Guid id, Guid variantId, CancellationToken cancellationToken)
    {
        var product = await LoadForChangeAsync(id, cancellationToken);
        FindVariant(product, variantId);

        EnsureAllowed(product.CheckVariantArchive(variantId));
        product.ArchiveVariant(variantId, timeProvider.GetUtcNow());

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<AdminProductResponse> AttachImageAsync(Guid id, AttachProductImageRequest request, CancellationToken cancellationToken)
    {
        var product = await LoadForChangeAsync(id, cancellationToken);
        if (product.Images.Count >= Product.MaxImages)
        {
            throw new ConflictException(ErrorTexts.ImageLimitReachedDetail, ErrorCodes.ImageLimitReached, ErrorTexts.ImageLimitReachedTitle);
        }

        // Validates the key, makes sure the renditions exist (processing the upload here in local development) and reads their size.
        var processed = await images.GetProcessedAsync(request.UploadKey!, MediaPurpose.Product, "uploadKey", cancellationToken);
        MediaKeys.TryParseUploadKey(request.UploadKey, out _, out var imageId);

        if (await db.ProductImages.AnyAsync(i => i.Id == imageId, cancellationToken))
        {
            throw new ConflictException(ErrorTexts.ImageAlreadyAttachedDetail, ErrorCodes.ImageAlreadyAttached, ErrorTexts.ImageAlreadyAttachedTitle);
        }

        var image = product.AddImage(imageId, processed.StorageKey, request.AltText, processed.Width, processed.Height, timeProvider.GetUtcNow());
        db.ProductImages.Add(image);

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<AdminProductResponse> ReorderImagesAsync(Guid id, ReorderProductImagesRequest request, CancellationToken cancellationToken)
    {
        var product = await LoadForChangeAsync(id, cancellationToken);
        var ids = request.ImageIds!;

        if (ids.Count != product.Images.Count || ids.Any(imageId => product.Images.All(i => i.Id != imageId)))
        {
            throw new RequestValidationException("imageIds", "Redosled mora da sadrži sve slike proizvoda, svaku tačno jednom.");
        }

        if (request.MainImageId is { } mainId && !ids.Contains(mainId))
        {
            throw new RequestValidationException("mainImageId", "Glavna slika mora biti jedna od slika proizvoda.");
        }

        var previousMain = product.MainImage?.Id;
        product.ReorderImages(ids, request.MainImageId, timeProvider.GetUtcNow());

        await SaveImagesAsync(product, previousMain, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<AdminProductResponse> UpdateImageAsync(Guid id, Guid imageId, UpdateProductImageRequest request, CancellationToken cancellationToken)
    {
        var product = await LoadForChangeAsync(id, cancellationToken);
        FindImage(product, imageId);

        product.SetImageAltText(imageId, request.AltText, timeProvider.GetUtcNow());

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<AdminProductResponse> DeleteImageAsync(Guid id, Guid imageId, CancellationToken cancellationToken)
    {
        var product = await LoadForChangeAsync(id, cancellationToken);
        FindImage(product, imageId);

        var previousMain = product.MainImage?.Id;
        var removed = product.RemoveImage(imageId, timeProvider.GetUtcNow());
        db.ProductImages.Remove(removed);

        await SaveImagesAsync(product, previousMain, cancellationToken);

        // Files go after the record: a failure here leaves unused files, never a record without files.
        await images.DeleteRenditionsAsync(removed.StorageKey, removed.Width, cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    /// <summary>
    /// Saves image changes. When the main image changes, the old flag is cleared first inside the same transaction,
    /// because the "one main image per product" unique index is checked statement by statement.
    /// </summary>
    private async Task SaveImagesAsync(Product product, Guid? previousMainId, CancellationToken cancellationToken)
    {
        if (previousMainId is null || product.MainImage?.Id == previousMainId)
        {
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.ProductImages
            .Where(i => i.ProductId == product.Id && i.IsMain)
            .ExecuteUpdateAsync(setters => setters.SetProperty(i => i.IsMain, false), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<Product> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Products
            .Include(p => p.Variants)
            .Include(p => p.Images)
            .SingleOrDefaultAsync(p => p.Id == id, cancellationToken)
        ?? throw new NotFoundException();

    private async Task<Product> LoadForChangeAsync(Guid id, CancellationToken cancellationToken)
    {
        var product = await LoadAsync(id, cancellationToken);
        if (product.IsArchived)
        {
            throw new ConflictException(ErrorTexts.ProductArchivedDetail, ErrorCodes.ProductArchived, ErrorTexts.ProductArchivedTitle);
        }

        return product;
    }

    private static ProductVariant FindVariant(Product product, Guid variantId) =>
        product.Variants.FirstOrDefault(v => v.Id == variantId && !v.IsArchived) ?? throw new NotFoundException();

    private static ProductImage FindImage(Product product, Guid imageId) =>
        product.Images.FirstOrDefault(i => i.Id == imageId) ?? throw new NotFoundException();

    private static void EnsureAllowed(VariantRule rule)
    {
        switch (rule)
        {
            case VariantRule.LastVariant:
                throw new ConflictException(ErrorTexts.LastVariantDetail, ErrorCodes.LastVariant, ErrorTexts.LastVariantTitle);
            case VariantRule.LastActiveVariant:
                throw new ConflictException(ErrorTexts.LastActiveVariantDetail, ErrorCodes.LastActiveVariant, ErrorTexts.LastActiveVariantTitle);
        }
    }

    private ProductValues ToValues(ProductRequest request, Guid categoryId, string slug) => new(
        categoryId,
        request.Name!,
        slug,
        request.ShortDescription,
        sanitizer.Sanitize(request.Description),
        request.Sku!,
        request.IsActive,
        request.IsFeatured,
        request.SeoTitle,
        request.SeoDescription);

    private async Task EnsureCategoryUsableAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        if (!await db.Categories.AnyAsync(c => c.Id == categoryId && c.DeletedAt == null, cancellationToken))
        {
            throw new RequestValidationException("categoryId", "Izabrana kategorija ne postoji.");
        }
    }

    private Task<bool> SlugTakenAsync(string slug, Guid? exceptId, CancellationToken cancellationToken) =>
        db.Products.AnyAsync(p => p.Slug == slug && p.DeletedAt == null && p.Id != exceptId, cancellationToken);

    /// <summary>409 <c>SKU_TAKEN</c> with <c>errors.sku</c> when the base SKU is used by another product that is not archived.</summary>
    private async Task EnsureProductSkuFreeAsync(string sku, Guid? exceptId, CancellationToken cancellationToken)
    {
        var trimmed = sku.Trim();
        if (await db.Products.AnyAsync(p => p.Sku == trimmed && p.DeletedAt == null && p.Id != exceptId, cancellationToken))
        {
            throw SkuTaken([(trimmed, "sku")]);
        }
    }

    /// <summary>
    /// 409 <c>SKU_TAKEN</c> when any of the variant SKUs is used by another variant that is not archived. The <c>errors</c>
    /// map has one entry per taken SKU under its field path (<c>sku</c>, or <c>variants[i].sku</c> in a create-product request).
    /// </summary>
    private async Task EnsureVariantSkusFreeAsync(
        IReadOnlyList<(string Sku, string Field)> skus, Guid? exceptVariantId, CancellationToken cancellationToken)
    {
        var trimmed = skus.Select(s => s.Sku.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var taken = await db.ProductVariants
            .Where(v => trimmed.Contains(v.Sku) && v.DeletedAt == null && v.Id != exceptVariantId)
            .Select(v => v.Sku)
            .ToListAsync(cancellationToken);

        if (taken.Count > 0)
        {
            throw SkuTaken([.. skus.Select(s => (Sku: s.Sku.Trim(), s.Field)).Where(s => taken.Contains(s.Sku, StringComparer.Ordinal))]);
        }
    }

    private static IReadOnlyList<(string Sku, string Field)> VariantSkuFields(CreateProductRequest request) =>
        [.. request.Variants.Select((variant, index) => (variant.Sku, $"variants[{index}].sku"))];

    private static string SkuTakenMessage(string sku) => $"Šifra (SKU) „{sku}“ se već koristi. Izaberite drugu.";

    private static ConflictException SkuTaken(IReadOnlyList<(string Sku, string Field)> taken) => new(
        SkuTakenMessage(taken[0].Sku),
        ErrorCodes.SkuTaken,
        ErrorTexts.SkuTakenTitle,
        taken.ToDictionary(t => t.Field, t => new[] { SkuTakenMessage(t.Sku) }, StringComparer.Ordinal));
}
