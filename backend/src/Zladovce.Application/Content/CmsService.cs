using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Application.Media;
using Zladovce.Domain.Content;

namespace Zladovce.Application.Content;

public interface ICmsService
{
    Task<CmsPageListResponse> ListAsync(CancellationToken cancellationToken);

    Task<AdminHomePageResponse> GetHomeAdminAsync(CancellationToken cancellationToken);
    Task<AdminHomePageResponse> SaveHomeAsync(SaveHomePageRequest request, CancellationToken cancellationToken);
    Task<AdminAboutPageResponse> GetAboutAdminAsync(CancellationToken cancellationToken);
    Task<AdminAboutPageResponse> SaveAboutAsync(SaveAboutPageRequest request, CancellationToken cancellationToken);
    Task<AdminRichTextPageResponse> GetRichTextAdminAsync(CmsPageKey key, CancellationToken cancellationToken);
    Task<AdminRichTextPageResponse> SaveRichTextAsync(CmsPageKey key, SaveRichTextPageRequest request, CancellationToken cancellationToken);

    /// <summary>Public pages: 404 while the page has no content.</summary>
    Task<HomePageResponse> GetHomeAsync(CancellationToken cancellationToken);
    Task<AboutPageResponse> GetAboutAsync(CancellationToken cancellationToken);
    Task<RichTextPageResponse> GetRichTextAsync(CmsPageKey key, CancellationToken cancellationToken);
}

/// <summary>
/// CMS pages with structured JSON content per key. Texts are trimmed; rich text is sanitised with the blog allow-list; images
/// are processed uploads (purpose <c>page</c>). A page whose content is entirely empty is stored with null content (public 404).
/// Saves are audited by the interceptor (<c>CmsPage.Updated</c>, content changes summarised).
/// </summary>
public sealed class CmsService(
    IZladovceDbContext db,
    IRichHtmlSanitizer sanitizer,
    IImageProcessor images,
    IRequestContext requestContext,
    IOptions<MediaOptions> mediaOptions,
    TimeProvider timeProvider) : ICmsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly MediaOptions _media = mediaOptions.Value;

    public async Task<CmsPageListResponse> ListAsync(CancellationToken cancellationToken)
    {
        var pages = await db.CmsPages.AsNoTracking().ToListAsync(cancellationToken);
        var order = Enum.GetValues<CmsPageKey>().Select(CmsPageKeys.ToKey).ToList();
        return new CmsPageListResponse([.. pages.OrderBy(p => order.IndexOf(p.Key)).Select(p => new CmsPageSummary(p.Key, p.Title, p.HasContent, p.UpdatedAt))]);
    }

    // ---------------- home

    public async Task<AdminHomePageResponse> GetHomeAdminAsync(CancellationToken cancellationToken)
    {
        var (page, by) = await LoadAsync(CmsPageKey.Home, cancellationToken);
        return new AdminHomePageResponse(page.Key, page.Title, page.SeoTitle, page.SeoDescription, page.HasContent, Read<HomePageContent>(page)?.Map(Refresh),
            page.UpdatedAt, by, page.Version);
    }

    public async Task<AdminHomePageResponse> SaveHomeAsync(SaveHomePageRequest request, CancellationToken cancellationToken)
    {
        var page = await LoadForChangeAsync(CmsPageKey.Home, request.Version!.Value, cancellationToken);
        var existing = Read<HomePageContent>(page);
        HomePageContent? content = null;
        if (request.Content is { } input)
        {
            var hero = input.Hero is { } h
                ? new HomeHero(T(h.Title), T(h.Subtitle), T(h.PrimaryCtaLabel), T(h.SecondaryCtaLabel), await ImageAsync(h.Image, "content.hero.image", cancellationToken))
                : null;
            var story = input.Story is { } s ? new HomeStory(T(s.Title), T(s.Text), await ImageAsync(s.Image, "content.story.image", cancellationToken)) : null;
            var whyUs = input.WhyUs is { } w ? new HomeWhyUs(T(w.Title), [.. (w.Items ?? []).Select(i => new HomeWhyUsItem(i.Title!.Trim(), T(i.Text)))]) : null;
            content = new HomePageContent(
                hero is { Title: null, Subtitle: null, PrimaryCtaLabel: null, SecondaryCtaLabel: null, Image: null } ? null : hero,
                Section(input.Intro),
                story is { Title: null, Text: null, Image: null } ? null : story,
                whyUs is { Title: null, Items.Count: 0 } ? null : whyUs,
                Section(input.Quality));
            if (content is { Hero: null, Intro: null, Story: null, WhyUs: null, Quality: null })
            {
                content = null;
            }
        }

        await SaveAsync(page, request.Title, content, request.SeoTitle, request.SeoDescription, cancellationToken);
        await DeleteUnusedAsync(Images(existing), Images(content), cancellationToken);
        return await GetHomeAdminAsync(cancellationToken);
    }

    public async Task<HomePageResponse> GetHomeAsync(CancellationToken cancellationToken)
    {
        var (page, _) = await LoadAsync(CmsPageKey.Home, cancellationToken);
        var content = Read<HomePageContent>(page) ?? throw new NotFoundException();
        return new HomePageResponse(page.Key, page.Title, content.Map(Refresh), page.SeoTitle, page.SeoDescription, page.UpdatedAt);
    }

    // ---------------- about

    public async Task<AdminAboutPageResponse> GetAboutAdminAsync(CancellationToken cancellationToken)
    {
        var (page, by) = await LoadAsync(CmsPageKey.About, cancellationToken);
        var content = Read<AboutPageContent>(page);
        return new AdminAboutPageResponse(page.Key, page.Title, page.SeoTitle, page.SeoDescription, page.HasContent,
            content is null ? null : content with { Images = [.. content.Images.Select(Refresh)] }, page.UpdatedAt, by, page.Version);
    }

    public async Task<AdminAboutPageResponse> SaveAboutAsync(SaveAboutPageRequest request, CancellationToken cancellationToken)
    {
        var page = await LoadForChangeAsync(CmsPageKey.About, request.Version!.Value, cancellationToken);
        var existing = Read<AboutPageContent>(page);
        AboutPageContent? content = null;
        if (request.Content is { } input)
        {
            var list = new List<CmsImage>();
            var index = 0;
            foreach (var image in input.Images ?? [])
            {
                list.Add((await ImageAsync(image, $"content.images[{index++}]", cancellationToken))!);
            }

            var body = sanitizer.Sanitize(input.BodyHtml);
            content = body.Length == 0 && list.Count == 0 ? null : new AboutPageContent(body, list);
        }

        await SaveAsync(page, request.Title, content, request.SeoTitle, request.SeoDescription, cancellationToken);
        await DeleteUnusedAsync(existing?.Images ?? [], content?.Images ?? [], cancellationToken);
        return await GetAboutAdminAsync(cancellationToken);
    }

    public async Task<AboutPageResponse> GetAboutAsync(CancellationToken cancellationToken)
    {
        var (page, _) = await LoadAsync(CmsPageKey.About, cancellationToken);
        var content = Read<AboutPageContent>(page) ?? throw new NotFoundException();
        return new AboutPageResponse(page.Key, page.Title, content with { Images = [.. content.Images.Select(Refresh)] }, page.SeoTitle, page.SeoDescription, page.UpdatedAt);
    }

    // ---------------- privacy, terms, delivery

    public async Task<AdminRichTextPageResponse> GetRichTextAdminAsync(CmsPageKey key, CancellationToken cancellationToken)
    {
        EnsureRichText(key);
        var (page, by) = await LoadAsync(key, cancellationToken);
        return new AdminRichTextPageResponse(page.Key, page.Title, page.SeoTitle, page.SeoDescription, page.HasContent, Read<RichTextPageContent>(page),
            page.UpdatedAt, by, page.Version);
    }

    public async Task<AdminRichTextPageResponse> SaveRichTextAsync(CmsPageKey key, SaveRichTextPageRequest request, CancellationToken cancellationToken)
    {
        EnsureRichText(key);
        var page = await LoadForChangeAsync(key, request.Version!.Value, cancellationToken);
        var body = sanitizer.Sanitize(request.Content?.BodyHtml);
        await SaveAsync(page, request.Title, body.Length == 0 ? null : new RichTextPageContent(body), request.SeoTitle, request.SeoDescription, cancellationToken);
        return await GetRichTextAdminAsync(key, cancellationToken);
    }

    public async Task<RichTextPageResponse> GetRichTextAsync(CmsPageKey key, CancellationToken cancellationToken)
    {
        EnsureRichText(key);
        var (page, _) = await LoadAsync(key, cancellationToken);
        var content = Read<RichTextPageContent>(page) ?? throw new NotFoundException();
        return new RichTextPageResponse(page.Key, page.Title, content, page.SeoTitle, page.SeoDescription, page.UpdatedAt);
    }

    // ---------------- helpers

    private static void EnsureRichText(CmsPageKey key)
    {
        if (key is CmsPageKey.Home or CmsPageKey.About)
        {
            throw new NotFoundException();
        }
    }

    private static string? T(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static CmsTextSection? Section(CmsTextSectionInput? input) =>
        input is null || (T(input.Title) is null && T(input.Text) is null) ? null : new CmsTextSection(T(input.Title), T(input.Text));

    private static IReadOnlyList<CmsImage> Images(HomePageContent? content) =>
        content is null ? [] : new[] { content.Hero?.Image, content.Story?.Image }.OfType<CmsImage>().ToList();

    private CmsImage Refresh(CmsImage image) => image with { BaseUrl = _media.UrlFor(image.Key), Widths = RenditionPlan.WidthsFor(image.Width) };

    private async Task<CmsImage?> ImageAsync(CmsImageInput? input, string field, CancellationToken cancellationToken)
    {
        if (input?.Key is not { } key)
        {
            return null;
        }

        var alt = T(input.Alt);
        if (MediaKeys.IsStorageKey(key, MediaPurpose.Page, out _))
        {
            // An image already on a page keeps its stored size; anything else must be a fresh upload.
            var known = await KnownImagesAsync(cancellationToken);
            if (known.FirstOrDefault(i => i.Key == key) is { } existing)
            {
                return existing with { Alt = alt };
            }

            throw new RequestValidationException(field, "Slika nije pronađena. Otpremite je ponovo.");
        }

        var processed = await images.GetProcessedAsync(key, MediaPurpose.Page, field, cancellationToken);
        return new CmsImage(processed.StorageKey, _media.UrlFor(processed.StorageKey), RenditionPlan.WidthsFor(processed.Width), processed.Width, processed.Height, alt);
    }

    private List<CmsImage>? _knownImages;

    private async Task<List<CmsImage>> KnownImagesAsync(CancellationToken cancellationToken)
    {
        if (_knownImages is null)
        {
            var pages = await db.CmsPages.AsNoTracking().ToListAsync(cancellationToken);
            _knownImages = [.. pages.SelectMany(p => p.Key switch
            {
                "home" => Images(Read<HomePageContent>(p)),
                "about" => Read<AboutPageContent>(p)?.Images ?? [],
                _ => [],
            })];
        }

        return _knownImages;
    }

    private async Task DeleteUnusedAsync(IReadOnlyList<CmsImage> before, IReadOnlyList<CmsImage> after, CancellationToken cancellationToken)
    {
        foreach (var image in before.Where(b => after.All(a => a.Key != b.Key)))
        {
            await images.DeleteRenditionsAsync(image.Key, image.Width, cancellationToken);
        }
    }

    private static T? Read<T>(CmsPage page)
        where T : class =>
        page.ContentJson is null ? null : JsonSerializer.Deserialize<T>(page.ContentJson, JsonOptions);

    private async Task<(CmsPage Page, string? UpdatedBy)> LoadAsync(CmsPageKey key, CancellationToken cancellationToken)
    {
        var name = CmsPageKeys.ToKey(key);
        var page = await db.CmsPages.AsNoTracking().SingleOrDefaultAsync(p => p.Key == name, cancellationToken)
            ?? throw new InvalidOperationException($"The cms_pages row '{name}' is missing. Apply the database migrations.");
        var by = page.UpdatedByAdminId is { } id
            ? await db.AdminUsers.AsNoTracking().Where(a => a.Id == id).Select(a => a.FullName).SingleOrDefaultAsync(cancellationToken)
            : null;
        return (page, by);
    }

    private async Task<CmsPage> LoadForChangeAsync(CmsPageKey key, uint version, CancellationToken cancellationToken)
    {
        var name = CmsPageKeys.ToKey(key);
        var page = await db.CmsPages.SingleOrDefaultAsync(p => p.Key == name, cancellationToken)
            ?? throw new InvalidOperationException($"The cms_pages row '{name}' is missing. Apply the database migrations.");
        if (page.Version != version)
        {
            throw new ConflictException(ErrorTexts.ConcurrentEditDetail, ErrorCodes.ConcurrentEdit, ErrorTexts.ConcurrentEditTitle);
        }

        db.Entry(page).Property(p => p.Version).OriginalValue = version;
        return page;
    }

    private async Task SaveAsync(CmsPage page, string? title, object? content, string? seoTitle, string? seoDescription, CancellationToken cancellationToken)
    {
        var admin = requestContext.Admin ?? throw new ForbiddenException();
        page.Update(title, content is null ? null : JsonSerializer.Serialize(content, content.GetType(), JsonOptions), seoTitle, seoDescription, admin.Id, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }
}

internal static class HomePageContentExtensions
{
    public static HomePageContent Map(this HomePageContent content, Func<CmsImage, CmsImage> map) => content with
    {
        Hero = content.Hero is { Image: { } heroImage } hero ? hero with { Image = map(heroImage) } : content.Hero,
        Story = content.Story is { Image: { } storyImage } story ? story with { Image = map(storyImage) } : content.Story,
    };
}
