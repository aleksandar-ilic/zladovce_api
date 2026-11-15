using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Common.Paging;
using Zladovce.Application.Media;
using Zladovce.Domain.Content;

namespace Zladovce.Application.Content;

public interface IBlogAdminService
{
    Task<AdminBlogCategoryListResponse> ListCategoriesAsync(CancellationToken cancellationToken);
    Task<AdminBlogCategoryResponse> CreateCategoryAsync(SaveBlogCategoryRequest request, CancellationToken cancellationToken);
    Task<AdminBlogCategoryResponse> UpdateCategoryAsync(Guid id, SaveBlogCategoryRequest request, CancellationToken cancellationToken);
    Task DeleteCategoryAsync(Guid id, CancellationToken cancellationToken);

    Task<PagedResult<AdminBlogPostListItem>> ListPostsAsync(AdminBlogPostListCriteria criteria, CancellationToken cancellationToken);
    Task<AdminBlogPostResponse> GetPostAsync(Guid id, CancellationToken cancellationToken);
    Task<AdminBlogPostResponse> CreatePostAsync(SaveBlogPostRequest request, CancellationToken cancellationToken);
    Task<AdminBlogPostResponse> UpdatePostAsync(Guid id, SaveBlogPostRequest request, CancellationToken cancellationToken);
    Task<AdminBlogPostResponse> PublishAsync(Guid id, PublishBlogPostRequest request, CancellationToken cancellationToken);
    Task<AdminBlogPostResponse> UnpublishAsync(Guid id, UnpublishBlogPostRequest request, CancellationToken cancellationToken);
    Task ArchivePostAsync(Guid id, CancellationToken cancellationToken);
    Task<ContentImage> AddImageAsync(BlogImageRequest request, CancellationToken cancellationToken);
}

public interface IPublicBlogService
{
    Task<PublicBlogCategoryListResponse> ListCategoriesAsync(CancellationToken cancellationToken);

    /// <exception cref="NotFoundException">Unknown category slug.</exception>
    Task<PagedResult<PublicBlogPostListItem>> ListPostsAsync(string? categorySlug, PageRequest page, CancellationToken cancellationToken);
    Task<PublicBlogPostResponse> GetPostAsync(string slug, CancellationToken cancellationToken);
    Task<LatestBlogPostsResponse> LatestAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>The one definition of "public" for blog posts: Published, <c>published_at &lt;= now</c>, not archived.</summary>
public static class BlogVisibility
{
    public static IQueryable<BlogPost> PublicPosts(IZladovceDbContext db, DateTimeOffset now) =>
        db.BlogPosts.AsNoTracking().Where(p => p.Status == BlogPostStatus.Published && p.PublishedAt != null && p.PublishedAt <= now && p.DeletedAt == null);

    public static BlogPostState StateOf(BlogPostStatus status, DateTimeOffset? publishedAt, DateTimeOffset now) =>
        status != BlogPostStatus.Published ? BlogPostState.Draft
        : publishedAt is { } at && at <= now ? BlogPostState.Published
        : BlogPostState.Scheduled;
}

/// <summary>Blog administration. Changes are audited by the interceptor (<c>BlogPost.*</c>, <c>BlogCategory.*</c>).</summary>
public sealed class BlogAdminService(
    IZladovceDbContext db,
    IRichHtmlSanitizer sanitizer,
    IImageProcessor images,
    IRequestContext requestContext,
    IOptions<MediaOptions> mediaOptions,
    TimeProvider timeProvider) : IBlogAdminService
{
    private readonly MediaOptions _media = mediaOptions.Value;

    public async Task<AdminBlogCategoryListResponse> ListCategoriesAsync(CancellationToken cancellationToken)
    {
        var counts = await db.BlogPosts.AsNoTracking().Where(p => p.DeletedAt == null && p.CategoryId != null)
            .GroupBy(p => p.CategoryId!.Value).Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken);
        var categories = await db.BlogCategories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ThenBy(c => c.Id).ToListAsync(cancellationToken);
        return new AdminBlogCategoryListResponse([.. categories.Select(c => ToResponse(c, counts.GetValueOrDefault(c.Id)))]);
    }

    public async Task<AdminBlogCategoryResponse> CreateCategoryAsync(SaveBlogCategoryRequest request, CancellationToken cancellationToken)
    {
        var slug = await SlugAllocator.AllocateAsync(request.Slug, request.Name!, s => db.BlogCategories.AnyAsync(c => c.Slug == s, cancellationToken));
        var category = BlogCategory.Create(request.Name!, slug, request.SortOrder!.Value, timeProvider.GetUtcNow());
        db.BlogCategories.Add(category);
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(category, 0);
    }

    public async Task<AdminBlogCategoryResponse> UpdateCategoryAsync(Guid id, SaveBlogCategoryRequest request, CancellationToken cancellationToken)
    {
        var category = await db.BlogCategories.SingleOrDefaultAsync(c => c.Id == id, cancellationToken) ?? throw new NotFoundException();
        var slug = category.Slug;
        if (!string.IsNullOrWhiteSpace(request.Slug) && request.Slug != category.Slug)
        {
            slug = await SlugAllocator.AllocateAsync(request.Slug, request.Name!, s => db.BlogCategories.AnyAsync(c => c.Slug == s && c.Id != id, cancellationToken));
        }

        category.Update(request.Name!, slug, request.SortOrder!.Value, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(category, await db.BlogPosts.CountAsync(p => p.CategoryId == id && p.DeletedAt == null, cancellationToken));
    }

    public async Task DeleteCategoryAsync(Guid id, CancellationToken cancellationToken)
    {
        var category = await db.BlogCategories.SingleOrDefaultAsync(c => c.Id == id, cancellationToken) ?? throw new NotFoundException();
        if (await db.BlogPosts.AnyAsync(p => p.CategoryId == id && p.DeletedAt == null, cancellationToken))
        {
            throw new ConflictException(ErrorTexts.BlogCategoryHasPostsDetail, ErrorCodes.BlogCategoryHasPosts, ErrorTexts.BlogCategoryHasPostsTitle);
        }

        // Archived posts keep their history but lose the link, so the category row can go.
        var now = timeProvider.GetUtcNow();
        foreach (var archived in await db.BlogPosts.Where(p => p.CategoryId == id).ToListAsync(cancellationToken))
        {
            archived.DetachCategory(now);
        }

        db.BlogCategories.Remove(category);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<AdminBlogPostListItem>> ListPostsAsync(AdminBlogPostListCriteria criteria, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var query = db.BlogPosts.AsNoTracking().Where(p => p.DeletedAt == null);
        if (DbSearch.ContainsPattern(criteria.Search) is { } pattern)
        {
            query = query.Where(p => EF.Functions.Like(DbSearch.Unaccent(p.Title).ToLower(), DbSearch.Unaccent(pattern).ToLower())
                || EF.Functions.Like(DbSearch.Unaccent(p.Excerpt).ToLower(), DbSearch.Unaccent(pattern).ToLower()));
        }

        query = criteria.State switch
        {
            BlogPostState.Draft => query.Where(p => p.Status == BlogPostStatus.Draft),
            BlogPostState.Scheduled => query.Where(p => p.Status == BlogPostStatus.Published && p.PublishedAt > now),
            BlogPostState.Published => query.Where(p => p.Status == BlogPostStatus.Published && p.PublishedAt <= now),
            _ => query,
        };

        if (criteria.CategoryId is { } categoryId)
        {
            query = query.Where(p => p.CategoryId == categoryId);
        }

        var total = await query.CountAsync(cancellationToken);
        var posts = await query.OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.Id)
            .Skip(criteria.Page.Skip).Take(criteria.Page.PageSize).ToListAsync(cancellationToken);
        var (categories, authors) = await LookupsAsync(posts, cancellationToken);

        var items = posts.Select(p => new AdminBlogPostListItem(
            p.Id, p.Title, p.Slug, p.Excerpt, Cover(p), Category(p, categories), p.Status, BlogVisibility.StateOf(p.Status, p.PublishedAt, now),
            p.PublishedAt, Author(p, authors), p.CreatedAt, p.UpdatedAt)).ToList();
        return PagedResult<AdminBlogPostListItem>.Create(items, criteria.Page.Page, criteria.Page.PageSize, total);
    }

    public async Task<AdminBlogPostResponse> GetPostAsync(Guid id, CancellationToken cancellationToken)
    {
        var post = await db.BlogPosts.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, cancellationToken)
            ?? throw new NotFoundException();
        var (categories, authors) = await LookupsAsync([post], cancellationToken);
        var now = timeProvider.GetUtcNow();
        return new AdminBlogPostResponse(
            post.Id, post.Title, post.Slug, post.Excerpt, Cover(post), post.ContentHtml, Category(post, categories), post.Status,
            BlogVisibility.StateOf(post.Status, post.PublishedAt, now), post.PublishedAt, post.SeoTitle, post.SeoDescription, Author(post, authors),
            post.CreatedAt, post.UpdatedAt, post.Version);
    }

    public async Task<AdminBlogPostResponse> CreatePostAsync(SaveBlogPostRequest request, CancellationToken cancellationToken)
    {
        var admin = requestContext.Admin ?? throw new ForbiddenException();
        await EnsureCategoryAsync(request.CategoryId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var slug = await SlugAllocator.AllocateAsync(request.Slug, request.Title!, s => SlugTakenAsync(s, null, cancellationToken));
        var post = BlogPost.CreateDraft(admin.Id, request.CategoryId, request.Title!, slug, request.Excerpt ?? string.Empty,
            sanitizer.Sanitize(request.ContentHtml), request.SeoTitle, request.SeoDescription, now);
        await ApplyCoverAsync(post, request.CoverImageKey, now, cancellationToken);
        db.BlogPosts.Add(post);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return await GetPostAsync(post.Id, cancellationToken);
    }

    public async Task<AdminBlogPostResponse> UpdatePostAsync(Guid id, SaveBlogPostRequest request, CancellationToken cancellationToken)
    {
        if (request.Version is null)
        {
            throw new RequestValidationException("version", "Verzija je obavezna.");
        }

        var post = await LoadForChangeAsync(id, request.Version.Value, cancellationToken);
        await EnsureCategoryAsync(request.CategoryId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var slug = post.Slug;
        if (!string.IsNullOrWhiteSpace(request.Slug) && request.Slug != post.Slug)
        {
            slug = await SlugAllocator.AllocateAsync(request.Slug, request.Title!, s => SlugTakenAsync(s, id, cancellationToken));
        }

        var content = sanitizer.Sanitize(request.ContentHtml);
        if (post.Status == BlogPostStatus.Published)
        {
            EnsurePublishable(request.Excerpt, content);
        }

        post.Update(request.CategoryId, request.Title!, slug, request.Excerpt ?? string.Empty, content, request.SeoTitle, request.SeoDescription, now);
        var replaced = await ApplyCoverAsync(post, request.CoverImageKey, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (replaced is { } old)
        {
            await images.DeleteRenditionsAsync(old.Key, old.Width, cancellationToken);
        }

        db.ChangeTracker.Clear();
        return await GetPostAsync(id, cancellationToken);
    }

    public async Task<AdminBlogPostResponse> PublishAsync(Guid id, PublishBlogPostRequest request, CancellationToken cancellationToken)
    {
        var post = await LoadForChangeAsync(id, request.Version!.Value, cancellationToken);
        EnsurePublishable(post.Excerpt, post.ContentHtml);
        var now = timeProvider.GetUtcNow();
        post.Publish(request.PublishedAt ?? now, now);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return await GetPostAsync(id, cancellationToken);
    }

    public async Task<AdminBlogPostResponse> UnpublishAsync(Guid id, UnpublishBlogPostRequest request, CancellationToken cancellationToken)
    {
        var post = await LoadForChangeAsync(id, request.Version!.Value, cancellationToken);
        post.Unpublish(timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return await GetPostAsync(id, cancellationToken);
    }

    public async Task ArchivePostAsync(Guid id, CancellationToken cancellationToken)
    {
        var post = await db.BlogPosts.SingleOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, cancellationToken) ?? throw new NotFoundException();
        (string Key, int Width)? cover = post is { CoverImageKey: { } key, CoverImageWidth: { } width } ? (key, width) : null;
        post.Archive(timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        if (cover is { } old)
        {
            await images.DeleteRenditionsAsync(old.Key, old.Width, cancellationToken);
        }
    }

    public async Task<ContentImage> AddImageAsync(BlogImageRequest request, CancellationToken cancellationToken)
    {
        var image = await images.GetProcessedAsync(request.UploadKey!, MediaPurpose.Blog, "uploadKey", cancellationToken);
        return ContentImage.From(_media, image.StorageKey, image.Width, image.Height)!;
    }

    private static void EnsurePublishable(string? excerpt, string contentHtml)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(excerpt))
        {
            errors["excerpt"] = ["Objava mora imati uvod (kratak opis) da bi bila objavljena."];
        }

        if (string.IsNullOrWhiteSpace(contentHtml))
        {
            errors["contentHtml"] = ["Objava mora imati tekst da bi bila objavljena."];
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }
    }

    private async Task<BlogPost> LoadForChangeAsync(Guid id, uint version, CancellationToken cancellationToken)
    {
        var post = await db.BlogPosts.SingleOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, cancellationToken) ?? throw new NotFoundException();
        if (post.Version != version)
        {
            throw new ConflictException(ErrorTexts.ConcurrentEditDetail, ErrorCodes.ConcurrentEdit, ErrorTexts.ConcurrentEditTitle);
        }

        db.Entry(post).Property(p => p.Version).OriginalValue = version;
        return post;
    }

    private async Task EnsureCategoryAsync(Guid? categoryId, CancellationToken cancellationToken)
    {
        if (categoryId is { } id && !await db.BlogCategories.AnyAsync(c => c.Id == id, cancellationToken))
        {
            throw new RequestValidationException("categoryId", "Izabrana kategorija ne postoji.");
        }
    }

    private Task<bool> SlugTakenAsync(string slug, Guid? exceptId, CancellationToken cancellationToken) =>
        db.BlogPosts.AnyAsync(p => p.Slug == slug && p.DeletedAt == null && p.Id != exceptId, cancellationToken);

    private async Task<(string Key, int Width)?> ApplyCoverAsync(BlogPost post, string? coverKey, DateTimeOffset now, CancellationToken cancellationToken)
    {
        (string Key, int Width)? previous = post is { CoverImageKey: { } key, CoverImageWidth: { } width } ? (key, width) : null;
        if (string.IsNullOrWhiteSpace(coverKey))
        {
            post.RemoveCoverImage(now);
            return previous;
        }

        if (coverKey == post.CoverImageKey)
        {
            return null;
        }

        var image = await images.GetProcessedAsync(coverKey, MediaPurpose.Blog, "coverImageKey", cancellationToken);
        if (await db.BlogPosts.AnyAsync(p => p.CoverImageKey == image.StorageKey && p.Id != post.Id, cancellationToken))
        {
            throw new ConflictException(ErrorTexts.ImageAlreadyAttachedDetail, ErrorCodes.ImageAlreadyAttached, ErrorTexts.ImageAlreadyAttachedTitle);
        }

        post.SetCoverImage(image.StorageKey, image.Width, image.Height, now);
        return previous;
    }

    private async Task<(Dictionary<Guid, BlogCategory> Categories, Dictionary<Guid, string> Authors)> LookupsAsync(
        IReadOnlyCollection<BlogPost> posts, CancellationToken cancellationToken)
    {
        var categoryIds = posts.Where(p => p.CategoryId != null).Select(p => p.CategoryId!.Value).Distinct().ToList();
        var authorIds = posts.Select(p => p.AuthorAdminId).Distinct().ToList();
        var categories = await db.BlogCategories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
        var authors = await db.AdminUsers.AsNoTracking().Where(a => authorIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.FullName, cancellationToken);
        return (categories, authors);
    }

    private ContentImage? Cover(BlogPost p) => ContentImage.From(_media, p.CoverImageKey, p.CoverImageWidth, p.CoverImageHeight);

    private static BlogCategoryRef? Category(BlogPost p, Dictionary<Guid, BlogCategory> categories) =>
        p.CategoryId is { } id && categories.TryGetValue(id, out var c) ? new BlogCategoryRef(c.Id, c.Name, c.Slug) : null;

    private static BlogAuthorRef? Author(BlogPost p, Dictionary<Guid, string> authors) =>
        authors.TryGetValue(p.AuthorAdminId, out var name) ? new BlogAuthorRef(p.AuthorAdminId, name) : null;

    private static AdminBlogCategoryResponse ToResponse(BlogCategory c, int count) => new(c.Id, c.Name, c.Slug, c.SortOrder, count, c.CreatedAt, c.UpdatedAt);
}

/// <summary>The public blog: only posts of <see cref="BlogVisibility.PublicPosts"/>.</summary>
public sealed class PublicBlogService(IZladovceDbContext db, IOptions<MediaOptions> mediaOptions, TimeProvider timeProvider) : IPublicBlogService
{
    private readonly MediaOptions _media = mediaOptions.Value;

    public async Task<PublicBlogCategoryListResponse> ListCategoriesAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var counts = await BlogVisibility.PublicPosts(db, now).Where(p => p.CategoryId != null)
            .GroupBy(p => p.CategoryId!.Value).Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken);
        var categories = await db.BlogCategories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(cancellationToken);
        return new PublicBlogCategoryListResponse([.. categories.Select(c => new PublicBlogCategory(c.Name, c.Slug, counts.GetValueOrDefault(c.Id)))]);
    }

    public async Task<PagedResult<PublicBlogPostListItem>> ListPostsAsync(string? categorySlug, PageRequest page, CancellationToken cancellationToken)
    {
        var query = BlogVisibility.PublicPosts(db, timeProvider.GetUtcNow());
        if (!string.IsNullOrWhiteSpace(categorySlug))
        {
            var category = await db.BlogCategories.AsNoTracking().SingleOrDefaultAsync(c => c.Slug == categorySlug, cancellationToken)
                ?? throw new NotFoundException();
            query = query.Where(p => p.CategoryId == category.Id);
        }

        var total = await query.CountAsync(cancellationToken);
        var posts = await query.OrderByDescending(p => p.PublishedAt).ThenByDescending(p => p.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return PagedResult<PublicBlogPostListItem>.Create(await ToItemsAsync(posts, cancellationToken), page.Page, page.PageSize, total);
    }

    public async Task<LatestBlogPostsResponse> LatestAsync(int limit, CancellationToken cancellationToken)
    {
        var posts = await BlogVisibility.PublicPosts(db, timeProvider.GetUtcNow())
            .OrderByDescending(p => p.PublishedAt).ThenByDescending(p => p.Id).Take(limit).ToListAsync(cancellationToken);
        return new LatestBlogPostsResponse(await ToItemsAsync(posts, cancellationToken));
    }

    public async Task<PublicBlogPostResponse> GetPostAsync(string slug, CancellationToken cancellationToken)
    {
        var post = await BlogVisibility.PublicPosts(db, timeProvider.GetUtcNow()).SingleOrDefaultAsync(p => p.Slug == slug, cancellationToken)
            ?? throw new NotFoundException();
        var category = post.CategoryId is { } id ? await db.BlogCategories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken) : null;
        return new PublicBlogPostResponse(
            post.Title, post.Slug, post.Excerpt, ContentImage.From(_media, post.CoverImageKey, post.CoverImageWidth, post.CoverImageHeight), post.ContentHtml,
            category is null ? null : new PublicBlogCategoryRef(category.Name, category.Slug), post.PublishedAt!.Value, post.UpdatedAt, post.SeoTitle, post.SeoDescription);
    }

    private async Task<IReadOnlyList<PublicBlogPostListItem>> ToItemsAsync(List<BlogPost> posts, CancellationToken cancellationToken)
    {
        var ids = posts.Where(p => p.CategoryId != null).Select(p => p.CategoryId!.Value).Distinct().ToList();
        var categories = await db.BlogCategories.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
        return [.. posts.Select(p => new PublicBlogPostListItem(
            p.Title,
            p.Slug,
            p.Excerpt,
            ContentImage.From(_media, p.CoverImageKey, p.CoverImageWidth, p.CoverImageHeight),
            p.CategoryId is { } id && categories.TryGetValue(id, out var c) ? new PublicBlogCategoryRef(c.Name, c.Slug) : null,
            p.PublishedAt!.Value))];
    }
}
