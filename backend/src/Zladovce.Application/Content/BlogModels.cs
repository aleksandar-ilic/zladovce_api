using FluentValidation;
using Zladovce.Application.Common;
using Zladovce.Application.Media;
using Zladovce.Domain.Content;

namespace Zladovce.Application.Content;

/// <summary>A processed image of content (blog cover, image in a post, CMS image).</summary>
/// <param name="Key">Storage key; send it back to keep the image.</param>
/// <param name="BaseUrl">A rendition is at <c>{baseUrl}/{width}.webp</c>.</param>
/// <param name="Widths">Available rendition widths, ascending.</param>
/// <param name="Width">Width of the original in pixels.</param>
/// <param name="Height">Height of the original in pixels.</param>
public sealed record ContentImage(string Key, string BaseUrl, IReadOnlyList<int> Widths, int Width, int Height)
{
    public static ContentImage? From(MediaOptions options, string? key, int? width, int? height) =>
        key is not null && width is { } w && height is { } h
            ? new ContentImage(key, options.UrlFor(key), RenditionPlan.WidthsFor(w), w, h)
            : null;
}

/// <summary>Body of <c>POST /api/v1/admin/blog/categories</c> and <c>PUT …/{id}</c>. Every member must be sent.</summary>
public sealed record SaveBlogCategoryRequest
{
    /// <summary>1–120 characters.</summary>
    public required string? Name { get; init; }

    /// <summary>Create: null generates it from the name. Update: null keeps the current slug.</summary>
    public required string? Slug { get; init; }

    /// <summary>0 or more; lists are sorted by it, then by name.</summary>
    public required int? SortOrder { get; init; }
}

public sealed class SaveBlogCategoryRequestValidator : AbstractValidator<SaveBlogCategoryRequest>
{
    public SaveBlogCategoryRequestValidator()
    {
        RuleFor(r => r.Name)
            .Cascade(CascadeMode.Stop)
            .Must(n => !string.IsNullOrWhiteSpace(n)).WithMessage("Naziv je obavezan.")
            .Must(n => n!.Trim().Length <= BlogCategory.MaxNameLength).WithMessage($"Naziv može imati najviše {BlogCategory.MaxNameLength} znakova.");
        RuleFor(r => r.Slug).ValidSlug();
        RuleFor(r => r.SortOrder).NotNull().InclusiveBetween(0, 100_000).WithMessage("Redosled mora biti između 0 i 100000.");
    }
}

/// <param name="PostCount">Posts that are not archived (drafts included).</param>
public sealed record AdminBlogCategoryResponse(Guid Id, string Name, string Slug, int SortOrder, int PostCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record AdminBlogCategoryListResponse(IReadOnlyList<AdminBlogCategoryResponse> Items);

/// <summary>
/// Body of <c>POST /api/v1/admin/blog/posts</c> (creates a Draft) and <c>PUT /api/v1/admin/blog/posts/{id}</c>. Every member must
/// be sent; <c>version</c> only on update. Publication is changed with <c>publish</c>/<c>unpublish</c>, not here.
/// </summary>
public sealed record SaveBlogPostRequest
{
    /// <summary>Update only (ignored on create, may be null there): the <c>version</c> last read.</summary>
    public required uint? Version { get; init; }

    /// <summary>1–200 characters.</summary>
    public required string? Title { get; init; }

    /// <summary>Create: null generates it from the title. Update: null keeps the current slug.</summary>
    public required string? Slug { get; init; }

    /// <summary>Plain-text summary for lists and the homepage, up to 500 characters (required to publish).</summary>
    public required string? Excerpt { get; init; }

    /// <summary>Null removes the cover; the current <c>coverImage.key</c> keeps it; an upload key (purpose <c>blog</c>) replaces it.</summary>
    public required string? CoverImageKey { get; init; }

    /// <summary>
    /// Rich text (HTML). Sanitised on save: p, br, strong/b, em/i, u, s, ul/ol/li, h2–h4, blockquote, a[href,title], hr, figure,
    /// figcaption, img[src,alt,width,height] with <c>src</c> from this shop's media (see <c>POST /admin/blog/images</c>). Anything
    /// else is removed. Required to publish. Up to 200000 characters.
    /// </summary>
    public required string? ContentHtml { get; init; }

    /// <summary>A blog category id, or null for none.</summary>
    public required Guid? CategoryId { get; init; }

    /// <summary>Up to 120 characters.</summary>
    public required string? SeoTitle { get; init; }

    /// <summary>Up to 320 characters.</summary>
    public required string? SeoDescription { get; init; }
}

public sealed class SaveBlogPostRequestValidator : AbstractValidator<SaveBlogPostRequest>
{
    public SaveBlogPostRequestValidator()
    {
        RuleFor(r => r.Title)
            .Cascade(CascadeMode.Stop)
            .Must(t => !string.IsNullOrWhiteSpace(t)).WithMessage("Naslov je obavezan.")
            .Must(t => t!.Trim().Length <= BlogPost.MaxTitleLength).WithMessage($"Naslov može imati najviše {BlogPost.MaxTitleLength} znakova.");
        RuleFor(r => r.Slug).ValidSlug();
        RuleFor(r => r.Excerpt).MaximumLength(BlogPost.MaxExcerptLength).WithMessage($"Uvod može imati najviše {BlogPost.MaxExcerptLength} znakova.");
        RuleFor(r => r.CoverImageKey).MaximumLength(200);
        RuleFor(r => r.ContentHtml).MaximumLength(BlogPost.MaxContentLength).WithMessage("Tekst je predugačak.");
        RuleFor(r => r.SeoTitle).MaximumLength(120).WithMessage("SEO naslov može imati najviše 120 znakova.");
        RuleFor(r => r.SeoDescription).MaximumLength(320).WithMessage("SEO opis može imati najviše 320 znakova.");
    }
}

/// <summary>Body of <c>POST /api/v1/admin/blog/posts/{id}/publish</c>.</summary>
public sealed record PublishBlogPostRequest
{
    public required uint? Version { get; init; }

    /// <summary>Null = now. A future moment schedules the post: it becomes public then (state <c>Scheduled</c> until).</summary>
    public required DateTimeOffset? PublishedAt { get; init; }
}

public sealed class PublishBlogPostRequestValidator : AbstractValidator<PublishBlogPostRequest>
{
    public PublishBlogPostRequestValidator() => RuleFor(r => r.Version).NotNull().WithMessage("Verzija je obavezna.");
}

/// <summary>Body of <c>POST /api/v1/admin/blog/posts/{id}/unpublish</c>.</summary>
public sealed record UnpublishBlogPostRequest
{
    public required uint? Version { get; init; }
}

public sealed class UnpublishBlogPostRequestValidator : AbstractValidator<UnpublishBlogPostRequest>
{
    public UnpublishBlogPostRequestValidator() => RuleFor(r => r.Version).NotNull().WithMessage("Verzija je obavezna.");
}

/// <summary>Body of <c>POST /api/v1/admin/blog/images</c>: an uploaded image (purpose <c>blog</c>) to use inside a post.</summary>
public sealed record BlogImageRequest
{
    public required string? UploadKey { get; init; }
}

public sealed class BlogImageRequestValidator : AbstractValidator<BlogImageRequest>
{
    public BlogImageRequestValidator() => RuleFor(r => r.UploadKey).NotEmpty().MaximumLength(200);
}

/// <summary>What the public sees of a post right now.</summary>
public enum BlogPostState
{
    /// <summary>Not public (status Draft).</summary>
    Draft,

    /// <summary>Published with a future <c>publishedAt</c>: becomes public then.</summary>
    Scheduled,

    /// <summary>Public.</summary>
    Published,
}

public sealed record BlogCategoryRef(Guid Id, string Name, string Slug);

public sealed record BlogAuthorRef(Guid Id, string FullName);

/// <summary>A post in the admin list (no content).</summary>
/// <param name="Status">Stored status: <c>Draft</c> or <c>Published</c>.</param>
/// <param name="State">Draft, Scheduled (Published, future date) or Published (public).</param>
public sealed record AdminBlogPostListItem(
    Guid Id,
    string Title,
    string Slug,
    string Excerpt,
    ContentImage? CoverImage,
    BlogCategoryRef? Category,
    BlogPostStatus Status,
    BlogPostState State,
    DateTimeOffset? PublishedAt,
    BlogAuthorRef? Author,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A post as the administration edits it.</summary>
public sealed record AdminBlogPostResponse(
    Guid Id,
    string Title,
    string Slug,
    string Excerpt,
    ContentImage? CoverImage,
    string ContentHtml,
    BlogCategoryRef? Category,
    BlogPostStatus Status,
    BlogPostState State,
    DateTimeOffset? PublishedAt,
    string? SeoTitle,
    string? SeoDescription,
    BlogAuthorRef? Author,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);

/// <param name="State">Filter: Draft, Scheduled or Published; null = all (archived posts are never listed).</param>
public sealed record AdminBlogPostListCriteria(string? Search, BlogPostState? State, Guid? CategoryId, PageRequest Page);

/// <summary>A blog category of the shop, with the number of public posts.</summary>
public sealed record PublicBlogCategory(string Name, string Slug, int PostCount);

public sealed record PublicBlogCategoryListResponse(IReadOnlyList<PublicBlogCategory> Items);

public sealed record PublicBlogCategoryRef(string Name, string Slug);

/// <summary>A public post in lists and homepage previews.</summary>
public sealed record PublicBlogPostListItem(
    string Title,
    string Slug,
    string Excerpt,
    ContentImage? CoverImage,
    PublicBlogCategoryRef? Category,
    DateTimeOffset PublishedAt);

/// <summary>A public post.</summary>
/// <param name="UpdatedAt">Last change (for <c>dateModified</c> and the sitemap).</param>
public sealed record PublicBlogPostResponse(
    string Title,
    string Slug,
    string Excerpt,
    ContentImage? CoverImage,
    string ContentHtml,
    PublicBlogCategoryRef? Category,
    DateTimeOffset PublishedAt,
    DateTimeOffset UpdatedAt,
    string? SeoTitle,
    string? SeoDescription);

public sealed record LatestBlogPostsResponse(IReadOnlyList<PublicBlogPostListItem> Items);
