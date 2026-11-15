using Zladovce.Domain.Common;

namespace Zladovce.Domain.Content;

/// <summary>A question and answer (table <c>faq_items</c>). Only published items are public, ordered by <see cref="SortOrder"/>.</summary>
public sealed class FaqItem
{
    public const int MaxQuestionLength = 300;
    public const int MaxAnswerLength = 4000;

    private FaqItem()
    {
    }

    public Guid Id { get; private set; }
    public string Question { get; private set; } = null!;

    /// <summary>Plain text; line breaks are kept.</summary>
    public string Answer { get; private set; } = null!;
    public int SortOrder { get; private set; }
    public bool IsPublished { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static FaqItem Create(string question, string answer, bool isPublished, int sortOrder, DateTimeOffset now)
    {
        var item = new FaqItem { Id = Guid.CreateVersion7(now), CreatedAt = now, SortOrder = Math.Max(0, sortOrder) };
        item.Update(question, answer, isPublished, now);
        return item;
    }

    public void Update(string question, string answer, bool isPublished, DateTimeOffset now)
    {
        var q = Text.NullIfBlank(question) ?? throw new DomainException("A question is required.");
        var a = Text.NullIfBlank(answer) ?? throw new DomainException("An answer is required.");
        if (q.Length > MaxQuestionLength || a.Length > MaxAnswerLength)
        {
            throw new DomainException("FAQ texts are too long.");
        }

        Question = q;
        Answer = a;
        IsPublished = isPublished;
        UpdatedAt = now;
    }

    public void SetSortOrder(int sortOrder, DateTimeOffset now)
    {
        if (SortOrder == sortOrder)
        {
            return;
        }

        SortOrder = Math.Max(0, sortOrder);
        UpdatedAt = now;
    }
}

/// <summary>
/// A real customer testimonial entered by the Owner (table <c>testimonials</c>). The homepage section is shown only when at least
/// one is published (A13); nothing is seeded.
/// </summary>
public sealed class Testimonial
{
    public const int MaxAuthorLength = 100;
    public const int MaxPlaceLength = 100;
    public const int MaxTextLength = 1000;

    private Testimonial()
    {
    }

    public Guid Id { get; private set; }
    public string AuthorName { get; private set; } = null!;
    public string? Place { get; private set; }
    public string Text { get; private set; } = null!;
    public bool IsPublished { get; private set; }
    public int SortOrder { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Testimonial Create(string authorName, string? place, string text, bool isPublished, int sortOrder, DateTimeOffset now)
    {
        var testimonial = new Testimonial { Id = Guid.CreateVersion7(now), CreatedAt = now };
        testimonial.Update(authorName, place, text, isPublished, sortOrder, now);
        return testimonial;
    }

    public void Update(string authorName, string? place, string text, bool isPublished, int sortOrder, DateTimeOffset now)
    {
        var author = Common.Text.NullIfBlank(authorName) ?? throw new DomainException("Author name is required.");
        var body = Common.Text.NullIfBlank(text) ?? throw new DomainException("Testimonial text is required.");
        var where = Common.Text.NullIfBlank(place);
        if (author.Length > MaxAuthorLength || body.Length > MaxTextLength || where is { Length: > MaxPlaceLength } || sortOrder < 0)
        {
            throw new DomainException("Invalid testimonial.");
        }

        AuthorName = author;
        Place = where;
        Text = body;
        IsPublished = isPublished;
        SortOrder = sortOrder;
        UpdatedAt = now;
    }
}

/// <summary>Editable pages (table <c>cms_pages</c>). The keys are fixed; each has its own JSON content shape.</summary>
public enum CmsPageKey
{
    /// <summary>Početna: hero, intro, family story, why us, quality.</summary>
    Home,

    /// <summary>O nama.</summary>
    About,

    /// <summary>Politika privatnosti.</summary>
    Privacy,

    /// <summary>Uslovi korišćenja.</summary>
    Terms,

    /// <summary>Dostava i plaćanje.</summary>
    Delivery,
}

public static class CmsPageKeys
{
    public static string ToKey(CmsPageKey key) => key.ToString().ToLowerInvariant();

    public static bool TryParse(string? value, out CmsPageKey key)
    {
        foreach (var candidate in Enum.GetValues<CmsPageKey>())
        {
            if (string.Equals(ToKey(candidate), value, StringComparison.Ordinal))
            {
                key = candidate;
                return true;
            }
        }

        key = default;
        return false;
    }
}

/// <summary>
/// One editable page. <see cref="ContentJson"/> is null while the page has no content: the public endpoint then answers 404,
/// so nothing is shown that the business did not write. The migration seeds every key with null content.
/// </summary>
public sealed class CmsPage
{
    public const int MaxTitleLength = 200;

    private CmsPage()
    {
    }

    public Guid Id { get; private set; }

    /// <summary><c>home</c>, <c>about</c>, <c>privacy</c>, <c>terms</c>, <c>delivery</c>.</summary>
    public string Key { get; private set; } = null!;
    public string? Title { get; private set; }

    /// <summary>jsonb, validated per key by the application; null = empty page.</summary>
    public string? ContentJson { get; private set; }
    public string? SeoTitle { get; private set; }
    public string? SeoDescription { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid? UpdatedByAdminId { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>.</summary>
    public uint Version { get; private set; }

    public bool HasContent => ContentJson is not null;

    public void Update(string? title, string? contentJson, string? seoTitle, string? seoDescription, Guid adminId, DateTimeOffset now)
    {
        var trimmed = Text.NullIfBlank(title);
        if (trimmed is { Length: > MaxTitleLength } || adminId == Guid.Empty)
        {
            throw new DomainException("Invalid page update.");
        }

        Title = trimmed;
        ContentJson = string.IsNullOrWhiteSpace(contentJson) ? null : contentJson;
        SeoTitle = Text.NullIfBlank(seoTitle);
        SeoDescription = Text.NullIfBlank(seoDescription);
        UpdatedByAdminId = adminId;
        UpdatedAt = now;
    }
}
