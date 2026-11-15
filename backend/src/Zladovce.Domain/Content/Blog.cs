using Zladovce.Domain.Common;

namespace Zladovce.Domain.Content;

/// <summary>A blog category (table <c>blog_categories</c>). Deleted (not archived) when no post uses it any more.</summary>
public sealed class BlogCategory
{
    public const int MaxNameLength = 120;

    private BlogCategory()
    {
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;

    /// <summary>Unique.</summary>
    public string Slug { get; private set; } = null!;
    public int SortOrder { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static BlogCategory Create(string name, string slug, int sortOrder, DateTimeOffset now)
    {
        var category = new BlogCategory { Id = Guid.CreateVersion7(now), CreatedAt = now };
        category.Update(name, slug, sortOrder, now);
        return category;
    }

    public void Update(string name, string slug, int sortOrder, DateTimeOffset now)
    {
        var trimmed = Text.NullIfBlank(name) ?? throw new DomainException("Blog category name is required.");
        if (trimmed.Length > MaxNameLength || !Common.Slug.IsValid(slug) || sortOrder < 0)
        {
            throw new DomainException("Invalid blog category.");
        }

        Name = trimmed;
        Slug = slug;
        SortOrder = sortOrder;
        UpdatedAt = now;
    }
}

/// <summary>Publication state stored in <c>blog_posts.status</c>.</summary>
public enum BlogPostStatus
{
    Draft,
    Published,
}

/// <summary>
/// A blog post (table <c>blog_posts</c>). Public only when <see cref="Status"/> is Published, <see cref="PublishedAt"/> is not in
/// the future and the post is not archived (<see cref="IsPublicAt"/>). <see cref="ContentHtml"/> is sanitised before it is set.
/// </summary>
public sealed class BlogPost
{
    public const int MaxTitleLength = 200;
    public const int MaxExcerptLength = 500;
    public const int MaxContentLength = 200_000;

    private BlogPost()
    {
    }

    public Guid Id { get; private set; }
    public Guid? CategoryId { get; private set; }
    public Guid AuthorAdminId { get; private set; }
    public string Title { get; private set; } = null!;
    public string Slug { get; private set; } = null!;
    public string Excerpt { get; private set; } = null!;

    /// <summary>Storage key of the cover renditions (<c>media/blog/{id}</c>).</summary>
    public string? CoverImageKey { get; private set; }
    public int? CoverImageWidth { get; private set; }
    public int? CoverImageHeight { get; private set; }
    public string ContentHtml { get; private set; } = null!;
    public BlogPostStatus Status { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public string? SeoTitle { get; private set; }
    public string? SeoDescription { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, used for optimistic concurrency.</summary>
    public uint Version { get; private set; }

    /// <summary>The public rule: published, publication time reached, not archived.</summary>
    public bool IsPublicAt(DateTimeOffset now) =>
        Status == BlogPostStatus.Published && PublishedAt is { } at && at <= now && DeletedAt is null;

    public static BlogPost CreateDraft(
        Guid authorAdminId, Guid? categoryId, string title, string slug, string excerpt, string contentHtml, string? seoTitle, string? seoDescription, DateTimeOffset now)
    {
        if (authorAdminId == Guid.Empty)
        {
            throw new DomainException("A blog post needs an author.");
        }

        var post = new BlogPost
        {
            Id = Guid.CreateVersion7(now),
            AuthorAdminId = authorAdminId,
            Status = BlogPostStatus.Draft,
            CreatedAt = now,
        };
        post.Update(categoryId, title, slug, excerpt, contentHtml, seoTitle, seoDescription, now);
        return post;
    }

    public void Update(Guid? categoryId, string title, string slug, string excerpt, string contentHtml, string? seoTitle, string? seoDescription, DateTimeOffset now)
    {
        EnsureNotArchived();
        var trimmedTitle = Text.NullIfBlank(title) ?? throw new DomainException("A blog post needs a title.");
        if (trimmedTitle.Length > MaxTitleLength || !Common.Slug.IsValid(slug))
        {
            throw new DomainException("Invalid blog post title or slug.");
        }

        var trimmedExcerpt = (excerpt ?? string.Empty).Trim();
        if (trimmedExcerpt.Length > MaxExcerptLength || (contentHtml ?? string.Empty).Length > MaxContentLength)
        {
            throw new DomainException("Blog post texts are too long.");
        }

        CategoryId = categoryId;
        Title = trimmedTitle;
        Slug = slug;
        Excerpt = trimmedExcerpt;
        ContentHtml = contentHtml ?? string.Empty;
        SeoTitle = Text.NullIfBlank(seoTitle);
        SeoDescription = Text.NullIfBlank(seoDescription);
        UpdatedAt = now;
    }

    public void SetCoverImage(string storageKey, int width, int height, DateTimeOffset now)
    {
        EnsureNotArchived();
        if (string.IsNullOrWhiteSpace(storageKey) || width <= 0 || height <= 0)
        {
            throw new DomainException("Invalid cover image.");
        }

        CoverImageKey = storageKey;
        CoverImageWidth = width;
        CoverImageHeight = height;
        UpdatedAt = now;
    }

    public void RemoveCoverImage(DateTimeOffset now)
    {
        if (CoverImageKey is null)
        {
            return;
        }

        CoverImageKey = null;
        CoverImageWidth = null;
        CoverImageHeight = null;
        UpdatedAt = now;
    }

    /// <summary>Publishes at <paramref name="publishedAt"/> (now or a future moment: scheduled).</summary>
    public void Publish(DateTimeOffset publishedAt, DateTimeOffset now)
    {
        EnsureNotArchived();
        if (string.IsNullOrWhiteSpace(Excerpt) || string.IsNullOrWhiteSpace(ContentHtml))
        {
            throw new DomainException("A published post needs an excerpt and content.");
        }

        Status = BlogPostStatus.Published;
        PublishedAt = publishedAt.ToUniversalTime();
        UpdatedAt = now;
    }

    /// <summary>Back to Draft; the publication date is cleared.</summary>
    public void Unpublish(DateTimeOffset now)
    {
        EnsureNotArchived();
        Status = BlogPostStatus.Draft;
        PublishedAt = null;
        UpdatedAt = now;
    }

    /// <summary>Soft delete: never public again; the slug is freed (partial unique index); the cover is released.</summary>
    public void Archive(DateTimeOffset now)
    {
        EnsureNotArchived();
        Status = BlogPostStatus.Draft;
        PublishedAt = null;
        CoverImageKey = null;
        CoverImageWidth = null;
        CoverImageHeight = null;
        DeletedAt = now;
        UpdatedAt = now;
    }

    /// <summary>The category was deleted while this archived post still pointed at it.</summary>
    public void DetachCategory(DateTimeOffset now)
    {
        CategoryId = null;
        UpdatedAt = now;
    }

    private void EnsureNotArchived()
    {
        if (DeletedAt is not null)
        {
            throw new DomainException("An archived blog post cannot be changed.");
        }
    }
}
