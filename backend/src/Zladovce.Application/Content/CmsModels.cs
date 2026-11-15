using FluentValidation;

namespace Zladovce.Application.Content;

// ---------------------------------------------------------------- images

/// <summary>An image of a CMS page as sent by the admin.</summary>
public sealed record CmsImageInput
{
    /// <summary>The current <c>image.key</c> keeps the image; an upload key (purpose <c>page</c>) adds a new one.</summary>
    public required string? Key { get; init; }

    /// <summary>Alternative text, up to 200 characters.</summary>
    public required string? Alt { get; init; }
}

public sealed class CmsImageInputValidator : AbstractValidator<CmsImageInput>
{
    public CmsImageInputValidator()
    {
        RuleFor(i => i.Key).NotEmpty().WithMessage("Slika je obavezna.").MaximumLength(200);
        RuleFor(i => i.Alt).MaximumLength(200).WithMessage("Opis slike može imati najviše 200 znakova.");
    }
}

/// <summary>A processed image of a CMS page. A rendition is at <c>{baseUrl}/{width}.webp</c>.</summary>
public sealed record CmsImage(string Key, string BaseUrl, IReadOnlyList<int> Widths, int Width, int Height, string? Alt);

// ---------------------------------------------------------------- home

/// <summary>Content of the homepage (<c>home</c>). Every text is plain text (line breaks kept), every member must be sent (null allowed).</summary>
public sealed record HomePageContentInput
{
    public required HomeHeroInput? Hero { get; init; }
    public required CmsTextSectionInput? Intro { get; init; }
    public required HomeStoryInput? Story { get; init; }
    public required HomeWhyUsInput? WhyUs { get; init; }
    public required CmsTextSectionInput? Quality { get; init; }
}

/// <summary>Hero: title ≤ 120, subtitle ≤ 300, button labels ≤ 40 (the buttons' targets are fixed by the shop).</summary>
public sealed record HomeHeroInput
{
    public required string? Title { get; init; }
    public required string? Subtitle { get; init; }
    public required string? PrimaryCtaLabel { get; init; }
    public required string? SecondaryCtaLabel { get; init; }
    public required CmsImageInput? Image { get; init; }
}

/// <summary>A titled text section: title ≤ 120, text ≤ 3000.</summary>
public sealed record CmsTextSectionInput
{
    public required string? Title { get; init; }
    public required string? Text { get; init; }
}

/// <summary>Family story: title ≤ 120, text ≤ 5000, optional image.</summary>
public sealed record HomeStoryInput
{
    public required string? Title { get; init; }
    public required string? Text { get; init; }
    public required CmsImageInput? Image { get; init; }
}

/// <summary>"Zašto mi": title ≤ 120 and 0–6 items.</summary>
public sealed record HomeWhyUsInput
{
    public required string? Title { get; init; }
    public required IReadOnlyList<HomeWhyUsItemInput>? Items { get; init; }
}

/// <summary>Item title ≤ 80 (required), text ≤ 400.</summary>
public sealed record HomeWhyUsItemInput
{
    public required string? Title { get; init; }
    public required string? Text { get; init; }
}

public sealed record HomeHero(string? Title, string? Subtitle, string? PrimaryCtaLabel, string? SecondaryCtaLabel, CmsImage? Image);

public sealed record CmsTextSection(string? Title, string? Text);

public sealed record HomeStory(string? Title, string? Text, CmsImage? Image);

public sealed record HomeWhyUsItem(string Title, string? Text);

public sealed record HomeWhyUs(string? Title, IReadOnlyList<HomeWhyUsItem> Items);

/// <summary>Stored and returned homepage content. Sections the owner left empty are null.</summary>
public sealed record HomePageContent(HomeHero? Hero, CmsTextSection? Intro, HomeStory? Story, HomeWhyUs? WhyUs, CmsTextSection? Quality);

// ---------------------------------------------------------------- about / rich text

/// <summary>Content of O nama (<c>about</c>): sanitised rich text and 0–6 images.</summary>
public sealed record AboutPageContentInput
{
    /// <summary>Rich text, the blog allow-list (images only from this shop's media); up to 50000 characters.</summary>
    public required string? BodyHtml { get; init; }

    public required IReadOnlyList<CmsImageInput>? Images { get; init; }
}

public sealed record AboutPageContent(string BodyHtml, IReadOnlyList<CmsImage> Images);

/// <summary>Content of <c>privacy</c>, <c>terms</c> and <c>delivery</c>: sanitised rich text up to 100000 characters.</summary>
public sealed record RichTextPageContentInput
{
    public required string? BodyHtml { get; init; }
}

public sealed record RichTextPageContent(string BodyHtml);

// ---------------------------------------------------------------- requests and responses

/// <summary>Body of <c>PUT /api/v1/admin/cms/pages/home</c>.</summary>
public sealed record SaveHomePageRequest
{
    public required uint? Version { get; init; }

    /// <summary>Page title (≤ 200), may be null.</summary>
    public required string? Title { get; init; }
    public required string? SeoTitle { get; init; }
    public required string? SeoDescription { get; init; }

    /// <summary>Null (or all sections empty) makes the page empty again: the public endpoint then answers 404.</summary>
    public required HomePageContentInput? Content { get; init; }
}

/// <summary>Body of <c>PUT /api/v1/admin/cms/pages/about</c>.</summary>
public sealed record SaveAboutPageRequest
{
    public required uint? Version { get; init; }
    public required string? Title { get; init; }
    public required string? SeoTitle { get; init; }
    public required string? SeoDescription { get; init; }

    /// <summary>Null or an empty body without images makes the page empty again.</summary>
    public required AboutPageContentInput? Content { get; init; }
}

/// <summary>Body of <c>PUT /api/v1/admin/cms/pages/{privacy|terms|delivery}</c>.</summary>
public sealed record SaveRichTextPageRequest
{
    public required uint? Version { get; init; }
    public required string? Title { get; init; }
    public required string? SeoTitle { get; init; }
    public required string? SeoDescription { get; init; }

    /// <summary>Null or an empty body makes the page empty again.</summary>
    public required RichTextPageContentInput? Content { get; init; }
}

/// <summary>Admin view of a page. <c>content</c> is null while the page is empty.</summary>
public sealed record AdminHomePageResponse(
    string Key, string? Title, string? SeoTitle, string? SeoDescription, bool HasContent, HomePageContent? Content, DateTimeOffset UpdatedAt, string? UpdatedByAdminName, uint Version);

public sealed record AdminAboutPageResponse(
    string Key, string? Title, string? SeoTitle, string? SeoDescription, bool HasContent, AboutPageContent? Content, DateTimeOffset UpdatedAt, string? UpdatedByAdminName, uint Version);

public sealed record AdminRichTextPageResponse(
    string Key, string? Title, string? SeoTitle, string? SeoDescription, bool HasContent, RichTextPageContent? Content, DateTimeOffset UpdatedAt, string? UpdatedByAdminName, uint Version);

/// <summary>Public homepage content (404 while empty).</summary>
public sealed record HomePageResponse(string Key, string? Title, HomePageContent Content, string? SeoTitle, string? SeoDescription, DateTimeOffset UpdatedAt);

public sealed record AboutPageResponse(string Key, string? Title, AboutPageContent Content, string? SeoTitle, string? SeoDescription, DateTimeOffset UpdatedAt);

public sealed record RichTextPageResponse(string Key, string? Title, RichTextPageContent Content, string? SeoTitle, string? SeoDescription, DateTimeOffset UpdatedAt);

/// <summary>Every page key and whether it has content (admin overview).</summary>
public sealed record CmsPageSummary(string Key, string? Title, bool HasContent, DateTimeOffset UpdatedAt);

public sealed record CmsPageListResponse(IReadOnlyList<CmsPageSummary> Items);

// ---------------------------------------------------------------- validators

internal static class CmsRules
{
    public static void Text<T>(IRuleBuilderInitial<T, string?> rule, int max, string label) =>
        rule.Must(v => v is null || v.Trim().Length <= max).WithMessage($"{label} može imati najviše {max} znakova.");

    public static void PageRules<T>(AbstractValidator<T> v, Func<T, uint?> version, Func<T, string?> title, Func<T, string?> seoTitle, Func<T, string?> seoDescription)
    {
        v.RuleFor(r => version(r)).NotNull().OverridePropertyName("version").WithMessage("Verzija je obavezna.");
        v.RuleFor(r => title(r)).MaximumLength(200).OverridePropertyName("title").WithMessage("Naslov može imati najviše 200 znakova.");
        v.RuleFor(r => seoTitle(r)).MaximumLength(120).OverridePropertyName("seoTitle").WithMessage("SEO naslov može imati najviše 120 znakova.");
        v.RuleFor(r => seoDescription(r)).MaximumLength(320).OverridePropertyName("seoDescription").WithMessage("SEO opis može imati najviše 320 znakova.");
    }
}

public sealed class SaveHomePageRequestValidator : AbstractValidator<SaveHomePageRequest>
{
    public SaveHomePageRequestValidator()
    {
        CmsRules.PageRules(this, r => r.Version, r => r.Title, r => r.SeoTitle, r => r.SeoDescription);
        RuleFor(r => r.Content!.Hero).ChildRules(h =>
        {
            CmsRules.Text(h.RuleFor(x => x!.Title), 120, "Naslov");
            CmsRules.Text(h.RuleFor(x => x!.Subtitle), 300, "Podnaslov");
            CmsRules.Text(h.RuleFor(x => x!.PrimaryCtaLabel), 40, "Tekst dugmeta");
            CmsRules.Text(h.RuleFor(x => x!.SecondaryCtaLabel), 40, "Tekst dugmeta");
            h.RuleFor(x => x!.Image!).SetValidator(new CmsImageInputValidator()).When(x => x!.Image is not null);
        }).When(r => r.Content?.Hero is not null);
        RuleFor(r => r.Content!.Intro).ChildRules(Section).When(r => r.Content?.Intro is not null);
        RuleFor(r => r.Content!.Quality).ChildRules(Section).When(r => r.Content?.Quality is not null);
        RuleFor(r => r.Content!.Story).ChildRules(s =>
        {
            CmsRules.Text(s.RuleFor(x => x!.Title), 120, "Naslov");
            CmsRules.Text(s.RuleFor(x => x!.Text), 5000, "Tekst");
            s.RuleFor(x => x!.Image!).SetValidator(new CmsImageInputValidator()).When(x => x!.Image is not null);
        }).When(r => r.Content?.Story is not null);
        RuleFor(r => r.Content!.WhyUs).ChildRules(w =>
        {
            CmsRules.Text(w.RuleFor(x => x!.Title), 120, "Naslov");
            w.RuleFor(x => x!.Items).NotNull().WithMessage("Lista je obavezna (može biti prazna).")
                .Must(i => i is null || i.Count <= 6).WithMessage("Najviše 6 stavki.");
            w.RuleForEach(x => x!.Items).NotNull().ChildRules(item =>
            {
                item.RuleFor(i => i.Title)
                    .Cascade(CascadeMode.Stop)
                    .Must(t => !string.IsNullOrWhiteSpace(t)).WithMessage("Naslov stavke je obavezan.")
                    .Must(t => t!.Trim().Length <= 80).WithMessage("Naslov stavke može imati najviše 80 znakova.");
                CmsRules.Text(item.RuleFor(i => i.Text), 400, "Tekst stavke");
            });
        }).When(r => r.Content?.WhyUs is not null);
    }

    private static void Section(InlineValidator<CmsTextSectionInput?> s)
    {
        CmsRules.Text(s.RuleFor(x => x!.Title), 120, "Naslov");
        CmsRules.Text(s.RuleFor(x => x!.Text), 3000, "Tekst");
    }
}

public sealed class SaveAboutPageRequestValidator : AbstractValidator<SaveAboutPageRequest>
{
    public SaveAboutPageRequestValidator()
    {
        CmsRules.PageRules(this, r => r.Version, r => r.Title, r => r.SeoTitle, r => r.SeoDescription);
        RuleFor(r => r.Content!.BodyHtml).MaximumLength(50_000).WithMessage("Tekst je predugačak.").When(r => r.Content is not null);
        RuleFor(r => r.Content!.Images).NotNull().WithMessage("Lista slika je obavezna (može biti prazna).")
            .Must(i => i is null || i.Count <= 6).WithMessage("Najviše 6 slika.").When(r => r.Content is not null);
        RuleForEach(r => r.Content!.Images).NotNull().SetValidator(new CmsImageInputValidator()!).When(r => r.Content?.Images is not null);
    }
}

public sealed class SaveRichTextPageRequestValidator : AbstractValidator<SaveRichTextPageRequest>
{
    public SaveRichTextPageRequestValidator()
    {
        CmsRules.PageRules(this, r => r.Version, r => r.Title, r => r.SeoTitle, r => r.SeoDescription);
        RuleFor(r => r.Content!.BodyHtml).MaximumLength(100_000).WithMessage("Tekst je predugačak.").When(r => r.Content is not null);
    }
}
