using Zladovce.Domain.Common;

namespace Zladovce.Domain.Categories;

/// <summary>Product category (table <c>categories</c>). Flat list; archived through <see cref="DeletedAt"/>.</summary>
public sealed class Category
{
    private Category()
    {
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string Slug { get; private set; } = null!;
    public string? Description { get; private set; }

    /// <summary>Base storage key of the processed image renditions (<c>media/categories/{imageId}</c>).</summary>
    public string? ImageKey { get; private set; }

    /// <summary>Width of the original image in pixels; decides which renditions exist.</summary>
    public int? ImageWidth { get; private set; }
    public int? ImageHeight { get; private set; }

    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; }
    public string? SeoTitle { get; private set; }
    public string? SeoDescription { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    public bool IsArchived => DeletedAt is not null;

    public static Category Create(
        string name,
        string slug,
        string? description,
        int sortOrder,
        bool isActive,
        string? seoTitle,
        string? seoDescription,
        DateTimeOffset now)
    {
        var category = new Category
        {
            Id = Guid.CreateVersion7(now),
            CreatedAt = now,
        };
        category.Apply(name, slug, description, sortOrder, isActive, seoTitle, seoDescription, now);
        return category;
    }

    public void Update(
        string name,
        string slug,
        string? description,
        int sortOrder,
        bool isActive,
        string? seoTitle,
        string? seoDescription,
        DateTimeOffset now)
    {
        EnsureNotArchived();
        Apply(name, slug, description, sortOrder, isActive, seoTitle, seoDescription, now);
    }

    public void SetSortOrder(int sortOrder, DateTimeOffset now)
    {
        EnsureNotArchived();
        if (sortOrder < 0)
        {
            throw new DomainException("Category sort order cannot be negative.");
        }

        if (SortOrder == sortOrder)
        {
            return;
        }

        SortOrder = sortOrder;
        UpdatedAt = now;
    }

    public void SetImage(string storageKey, int width, int height, DateTimeOffset now)
    {
        EnsureNotArchived();
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            throw new DomainException("Category image requires a storage key.");
        }

        if (width <= 0 || height <= 0)
        {
            throw new DomainException("Category image dimensions must be positive.");
        }

        ImageKey = storageKey;
        ImageWidth = width;
        ImageHeight = height;
        UpdatedAt = now;
    }

    public void RemoveImage(DateTimeOffset now)
    {
        EnsureNotArchived();
        if (ImageKey is null)
        {
            return;
        }

        ImageKey = null;
        ImageWidth = null;
        ImageHeight = null;
        UpdatedAt = now;
    }

    public void Archive(DateTimeOffset now)
    {
        if (IsArchived)
        {
            return;
        }

        DeletedAt = now;
        UpdatedAt = now;
    }

    private void Apply(
        string name,
        string slug,
        string? description,
        int sortOrder,
        bool isActive,
        string? seoTitle,
        string? seoDescription,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Category name is required.");
        }

        if (!Common.Slug.IsValid(slug))
        {
            throw new DomainException("Category slug is not valid.");
        }

        if (sortOrder < 0)
        {
            throw new DomainException("Category sort order cannot be negative.");
        }

        Name = name.Trim();
        Slug = slug;
        Description = Text.NullIfBlank(description);
        SortOrder = sortOrder;
        IsActive = isActive;
        SeoTitle = Text.NullIfBlank(seoTitle);
        SeoDescription = Text.NullIfBlank(seoDescription);
        UpdatedAt = now;
    }

    private void EnsureNotArchived()
    {
        if (IsArchived)
        {
            throw new DomainException("An archived category cannot be changed.");
        }
    }
}
