using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Zladovce.IntegrationTests.Infrastructure;

namespace Zladovce.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class ContentTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private readonly MinioFixture _minio;
    private readonly DevApiFactory _factory;
    private readonly HttpClient _client;
    private readonly AdminApi _admin;
    private readonly AdminApi _staff;

    public ContentTests(PostgresFixture postgres, MinioFixture minio)
    {
        _postgres = postgres;
        _minio = minio;
        _factory = new DevApiFactory { ConnectionString = postgres.ConnectionString, ExtraSettings = minio.Settings() };
        _client = _factory.CreateClient();
        _admin = new AdminApi(_client, "sadrzaj.owner@example.com");
        _staff = new AdminApi(_client, "sadrzaj.staff@example.com", "Staff");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static object Post(string title, string? content = "<p>Tekst</p>", string? excerpt = "Uvod", Guid? categoryId = null, uint? version = null, string? cover = null, string? slug = null) =>
        new { version, title, slug, excerpt, coverImageKey = cover, contentHtml = content, categoryId, seoTitle = (string?)null, seoDescription = (string?)null };

    private async Task<HttpStatusCode> PublicStatusAsync(string url) => (await _client.GetAsync(url)).StatusCode;

    private async Task<JsonElement> PublicAsync(string url)
    {
        var response = await _client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<bool> InSeoRoutesAsync(string slug) =>
        (await PublicAsync("/api/v1/seo/routes")).GetProperty("blogPosts").EnumerateArray().Any(r => r.GetProperty("slug").GetString() == slug);

    [Fact]
    public async Task A_draft_is_never_public_and_a_published_post_is()
    {
        var category = await _staff.JsonAsync(HttpMethod.Post, "/api/v1/admin/blog/categories", new { name = $"Recepti {Unique.Token()}", slug = (string?)null, sortOrder = 0 }, HttpStatusCode.Created);
        var created = await _staff.JsonAsync(HttpMethod.Post, "/api/v1/admin/blog/posts", Post($"Kako se pravi džem {Unique.Token()}", categoryId: category.Id()), HttpStatusCode.Created);
        var slug = created.GetProperty("slug").GetString()!;
        Assert.Equal("Draft", created.GetProperty("state").GetString());
        Assert.StartsWith("kako-se-pravi-dzem-", slug);

        Assert.Equal(HttpStatusCode.NotFound, await PublicStatusAsync($"/api/v1/blog/posts/{slug}"));
        Assert.DoesNotContain((await PublicAsync("/api/v1/blog/posts?pageSize=100")).Items(), p => p.GetProperty("slug").GetString() == slug);
        Assert.DoesNotContain((await PublicAsync("/api/v1/blog/latest-posts?limit=12")).Items(), p => p.GetProperty("slug").GetString() == slug);
        Assert.False(await InSeoRoutesAsync(slug));

        var published = await _staff.JsonAsync(HttpMethod.Post, $"/api/v1/admin/blog/posts/{created.Id()}/publish",
            new { version = created.GetProperty("version").GetUInt32(), publishedAt = (DateTimeOffset?)null });
        Assert.Equal("Published", published.GetProperty("state").GetString());

        var post = await PublicAsync($"/api/v1/blog/posts/{slug}");
        Assert.Equal("<p>Tekst</p>", post.GetProperty("contentHtml").GetString());
        Assert.Equal(category.GetProperty("slug").GetString(), post.GetProperty("category").GetProperty("slug").GetString());
        Assert.Contains((await PublicAsync($"/api/v1/blog/posts?category={category.GetProperty("slug").GetString()}")).Items(), p => p.GetProperty("slug").GetString() == slug);
        Assert.Equal(slug, (await PublicAsync("/api/v1/blog/latest-posts?limit=1")).Items()[0].GetProperty("slug").GetString());
        Assert.True(await InSeoRoutesAsync(slug));
        var seoCategories = (await PublicAsync("/api/v1/seo/routes")).GetProperty("blogCategories");
        Assert.Contains(seoCategories.EnumerateArray(), c => c.GetProperty("slug").GetString() == category.GetProperty("slug").GetString());

        // Unpublish: gone again everywhere.
        var unpublished = await _staff.JsonAsync(HttpMethod.Post, $"/api/v1/admin/blog/posts/{created.Id()}/unpublish",
            new { version = published.GetProperty("version").GetUInt32() });
        Assert.Equal("Draft", unpublished.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.NotFound, await PublicStatusAsync($"/api/v1/blog/posts/{slug}"));
        Assert.False(await InSeoRoutesAsync(slug));

        // Publish again, then archive: never public, slug free, not in the admin list.
        var again = await _staff.JsonAsync(HttpMethod.Post, $"/api/v1/admin/blog/posts/{created.Id()}/publish",
            new { version = unpublished.GetProperty("version").GetUInt32(), publishedAt = (DateTimeOffset?)null });
        await _staff.JsonAsync(HttpMethod.Delete, $"/api/v1/admin/blog/posts/{created.Id()}", null, HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, await PublicStatusAsync($"/api/v1/blog/posts/{slug}"));
        Assert.False(await InSeoRoutesAsync(slug));
        Assert.Equal(HttpStatusCode.NotFound, (await _staff.SendAsync(HttpMethod.Get, $"/api/v1/admin/blog/posts/{created.Id()}")).StatusCode);
        var reused = await _staff.JsonAsync(HttpMethod.Post, "/api/v1/admin/blog/posts", Post("Isti naslov", slug: slug), HttpStatusCode.Created);
        Assert.Equal(slug, reused.GetProperty("slug").GetString());
        Assert.NotEqual(0u, again.GetProperty("version").GetUInt32());

        // The category is still used by the reused post's sibling? No: the reused post has no category, so it can be deleted.
        await _staff.JsonAsync(HttpMethod.Delete, $"/api/v1/admin/blog/categories/{category.Id()}", null, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_scheduled_post_becomes_public_at_its_time()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new DevApiFactory { ConnectionString = _postgres.ConnectionString, TestServices = s => s.AddSingleton<TimeProvider>(clock) };
        using var client = factory.CreateClient();
        var admin = new AdminApi(client, "sadrzaj.owner@example.com");

        var created = await admin.JsonAsync(HttpMethod.Post, "/api/v1/admin/blog/posts", Post($"Zakazano {Unique.Token()}"), HttpStatusCode.Created);
        var slug = created.GetProperty("slug").GetString()!;
        var scheduled = await admin.JsonAsync(HttpMethod.Post, $"/api/v1/admin/blog/posts/{created.Id()}/publish",
            new { version = created.GetProperty("version").GetUInt32(), publishedAt = clock.GetUtcNow().AddHours(2) });
        Assert.Equal("Scheduled", scheduled.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/blog/posts/{slug}")).StatusCode);
        var list = await admin.GetAsync("/api/v1/admin/blog/posts?status=Scheduled&pageSize=100");
        Assert.Contains(list.Items(), p => p.Id() == created.Id());

        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/blog/posts/{slug}")).StatusCode);
        await admin.JsonAsync(HttpMethod.Delete, $"/api/v1/admin/blog/posts/{created.Id()}", null, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Blog_html_is_sanitised_on_save()
    {
        var own = $"{_minio.Options.BaseUrl}/media/blog/{Guid.NewGuid()}/800.webp";
        var html =
            "<p onclick=\"alert(1)\">Zdravo<script>alert('x')</script></p>" +
            "<a href=\"javascript:alert(1)\">klik</a><a href=\"https://zladovce.com/prodavnica\">prodavnica</a>" +
            $"<figure><img src=\"{own}\" alt=\"Tegla\" onerror=\"alert(1)\"><figcaption>Opis</figcaption></figure>" +
            "<img src=\"https://evil.example/x.png\"><img src=\"" + _minio.Options.BaseUrl + "/uploads/blog/x.png\">" +
            "<iframe src=\"https://evil.example\"></iframe><style>p{}</style><div class=\"x\" style=\"color:red\">tekst</div>";
        var created = await _admin.JsonAsync(HttpMethod.Post, "/api/v1/admin/blog/posts", Post($"Čišćenje {Unique.Token()}", content: html), HttpStatusCode.Created);
        var stored = created.GetProperty("contentHtml").GetString()!;

        Assert.Contains("<p>Zdravo</p>", stored);
        Assert.DoesNotContain("script", stored);
        Assert.DoesNotContain("onclick", stored);
        Assert.DoesNotContain("onerror", stored);
        Assert.DoesNotContain("javascript:", stored);
        Assert.Contains("href=\"https://zladovce.com/prodavnica\"", stored);
        Assert.Contains($"<img src=\"{own}\" alt=\"Tegla\">", stored);
        Assert.Contains("<figcaption>Opis</figcaption>", stored);
        Assert.DoesNotContain("evil.example", stored);
        Assert.DoesNotContain("/uploads/", stored);
        Assert.DoesNotContain("iframe", stored);
        Assert.DoesNotContain("style", stored);
        Assert.DoesNotContain("class=", stored);
        Assert.Contains("tekst", stored);

        // Publishing needs an excerpt and content.
        var empty = await _admin.JsonAsync(HttpMethod.Post, "/api/v1/admin/blog/posts", Post($"Prazno {Unique.Token()}", content: "<script>x</script>", excerpt: null), HttpStatusCode.Created);
        var refused = await _admin.SendAsync(HttpMethod.Post, $"/api/v1/admin/blog/posts/{empty.Id()}/publish", new { version = empty.GetProperty("version").GetUInt32(), publishedAt = (DateTimeOffset?)null });
        var text = await refused.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("\"excerpt\"", text);
        Assert.Contains("\"contentHtml\"", text);

        // Stale version.
        var stale = await _admin.SendAsync(HttpMethod.Put, $"/api/v1/admin/blog/posts/{created.Id()}", Post("Novi naslov", version: 1));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("CONCURRENT_EDIT", await stale.Content.ReadAsStringAsync());

        // Audit: content changes are summarised, not copied.
        var audit = await _admin.GetAsync($"/api/v1/admin/audit-logs?entityType=BlogPost&entityId={created.Id()}");
        var entry = audit.Items().Single();
        Assert.Equal("BlogPost.Created", entry.GetProperty("action").GetString());
        Assert.Matches("^\\(\\d+ znakova\\)$", entry.GetProperty("changes").GetProperty("contentHtml").GetProperty("new").GetString()!);
        Assert.StartsWith("Objava na blogu – dodavanje", entry.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Cover_and_inline_images_use_the_upload_flow()
    {
        var upload = await MediaTestSupport.UploadAsync(_admin, _client, "blog");
        var created = await _admin.JsonAsync(HttpMethod.Post, "/api/v1/admin/blog/posts", Post($"Sa slikom {Unique.Token()}", cover: upload), HttpStatusCode.Created);
        var cover = created.GetProperty("coverImage");
        var key = cover.GetProperty("key").GetString()!;
        Assert.StartsWith("media/blog/", key);
        Assert.True(await _minio.ExistsAsync($"{key}/400.webp"));

        var inline = await _admin.JsonAsync(HttpMethod.Post, "/api/v1/admin/blog/images", new { uploadKey = await MediaTestSupport.UploadAsync(_admin, _client, "blog") });
        Assert.StartsWith(_minio.Options.BaseUrl + "/media/blog/", inline.GetProperty("baseUrl").GetString());

        var wrongPurpose = await _admin.SendAsync(HttpMethod.Post, "/api/v1/admin/blog/images", new { uploadKey = await MediaTestSupport.UploadAsync(_admin, _client, "category") });
        Assert.Equal(HttpStatusCode.BadRequest, wrongPurpose.StatusCode);

        await _admin.JsonAsync(HttpMethod.Delete, $"/api/v1/admin/blog/posts/{created.Id()}", null, HttpStatusCode.NoContent);
        Assert.False(await _minio.ExistsAsync($"{key}/400.webp"));
    }

    [Fact]
    public async Task Blog_category_in_use_cannot_be_deleted()
    {
        var category = await _admin.JsonAsync(HttpMethod.Post, "/api/v1/admin/blog/categories", new { name = $"Vesti {Unique.Token()}", slug = (string?)null, sortOrder = 1 }, HttpStatusCode.Created);
        var post = await _admin.JsonAsync(HttpMethod.Post, "/api/v1/admin/blog/posts", Post($"Vest {Unique.Token()}", categoryId: category.Id()), HttpStatusCode.Created);
        var refused = await _admin.SendAsync(HttpMethod.Delete, $"/api/v1/admin/blog/categories/{category.Id()}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("BLOG_CATEGORY_HAS_POSTS", await refused.Content.ReadAsStringAsync());

        var taken = await _admin.SendAsync(HttpMethod.Post, "/api/v1/admin/blog/categories", new { name = "X", slug = category.GetProperty("slug").GetString(), sortOrder = 0 });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains("SLUG_TAKEN", await taken.Content.ReadAsStringAsync());

        await _admin.JsonAsync(HttpMethod.Delete, $"/api/v1/admin/blog/posts/{post.Id()}", null, HttpStatusCode.NoContent);
        await _admin.JsonAsync(HttpMethod.Delete, $"/api/v1/admin/blog/categories/{category.Id()}", null, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Cms_pages_are_empty_until_written_and_validated_per_key()
    {
        var pages = await _admin.GetAsync("/api/v1/admin/cms/pages");
        Assert.Equal(["home", "about", "privacy", "terms", "delivery"], pages.Items().Select(p => p.GetProperty("key").GetString()!));

        // Legal pages are seeded empty: the public endpoint answers 404 (nothing invented).
        var terms = await _admin.GetAsync("/api/v1/admin/cms/pages/terms");
        if (!terms.GetProperty("hasContent").GetBoolean())
        {
            Assert.Equal(HttpStatusCode.NotFound, await PublicStatusAsync("/api/v1/content/pages/terms"));
        }

        var about = await _admin.GetAsync("/api/v1/admin/cms/pages/about");
        var saved = await _admin.JsonAsync(HttpMethod.Put, "/api/v1/admin/cms/pages/about", new
        {
            version = about.GetProperty("version").GetUInt32(),
            title = "O nama",
            seoTitle = (string?)null,
            seoDescription = (string?)null,
            content = new { bodyHtml = "<h2>Porodica</h2><p>Naša priča<script>x</script></p>", images = Array.Empty<object>() },
        });
        Assert.True(saved.GetProperty("hasContent").GetBoolean());
        var page = await PublicAsync("/api/v1/content/pages/about");
        Assert.Equal("<h2>Porodica</h2><p>Naša priča</p>", page.GetProperty("content").GetProperty("bodyHtml").GetString());
        Assert.Contains((await PublicAsync("/api/v1/seo/routes")).GetProperty("pages").EnumerateArray(), p => p.GetProperty("key").GetString() == "about");

        // An empty body makes the page empty again.
        var cleared = await _admin.JsonAsync(HttpMethod.Put, "/api/v1/admin/cms/pages/about", new
        {
            version = saved.GetProperty("version").GetUInt32(),
            title = (string?)null,
            seoTitle = (string?)null,
            seoDescription = (string?)null,
            content = new { bodyHtml = "  ", images = Array.Empty<object>() },
        });
        Assert.False(cleared.GetProperty("hasContent").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, await PublicStatusAsync("/api/v1/content/pages/about"));
        Assert.DoesNotContain((await PublicAsync("/api/v1/seo/routes")).GetProperty("pages").EnumerateArray(), p => p.GetProperty("key").GetString() == "about");

        // Stale version.
        var stale = await _admin.SendAsync(HttpMethod.Put, "/api/v1/admin/cms/pages/about", new
        {
            version = saved.GetProperty("version").GetUInt32(), title = (string?)null, seoTitle = (string?)null, seoDescription = (string?)null, content = (object?)null,
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // Home: per-key validation with field paths.
        var home = await _admin.GetAsync("/api/v1/admin/cms/pages/home");
        var invalid = await _admin.SendAsync(HttpMethod.Put, "/api/v1/admin/cms/pages/home", new
        {
            version = home.GetProperty("version").GetUInt32(),
            title = (string?)null,
            seoTitle = (string?)null,
            seoDescription = (string?)null,
            content = new
            {
                hero = new { title = new string('a', 121), subtitle = (string?)null, primaryCtaLabel = "Prodavnica", secondaryCtaLabel = (string?)null, image = (object?)null },
                intro = (object?)null,
                story = (object?)null,
                whyUs = new { title = "Zašto mi", items = Enumerable.Range(0, 7).Select(i => new { title = i == 0 ? "" : $"Stavka {i}", text = (string?)null }).ToArray() },
                quality = (object?)null,
            },
        });
        var errors = await invalid.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("content.hero.title", errors);
        Assert.Contains("content.whyUs.items", errors);
        Assert.Contains("content.whyUs.items[0].title", errors);

        // A missing member is reported (send every field).
        var missing = await _admin.SendAsync(HttpMethod.Put, "/api/v1/admin/cms/pages/home", new
        {
            version = home.GetProperty("version").GetUInt32(), title = (string?)null, seoTitle = (string?)null, seoDescription = (string?)null,
            content = new { hero = (object?)null, intro = (object?)null, story = (object?)null, whyUs = (object?)null },
        });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("content.quality", await missing.Content.ReadAsStringAsync());

        var unknown = await _admin.SendAsync(HttpMethod.Get, "/api/v1/admin/cms/pages/kontakt");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Home_page_with_text_and_an_image()
    {
        var home = await _admin.GetAsync("/api/v1/admin/cms/pages/home");
        var upload = await MediaTestSupport.UploadAsync(_admin, _client, "page");
        var saved = await _admin.JsonAsync(HttpMethod.Put, "/api/v1/admin/cms/pages/home", new
        {
            version = home.GetProperty("version").GetUInt32(),
            title = (string?)null,
            seoTitle = (string?)null,
            seoDescription = (string?)null,
            content = new
            {
                hero = new { title = "  Domaći džemovi ", subtitle = (string?)null, primaryCtaLabel = "Prodavnica", secondaryCtaLabel = (string?)null, image = new { key = upload, alt = "Tegle" } },
                intro = new { title = (string?)null, text = (string?)null },
                story = (object?)null,
                whyUs = new { title = (string?)null, items = new[] { new { title = "Bez aditiva", text = "Samo voće i šećer." } } },
                quality = (object?)null,
            },
        });
        var content = saved.GetProperty("content");
        Assert.Equal("Domaći džemovi", content.GetProperty("hero").GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, content.GetProperty("intro").ValueKind); // empty section dropped
        var image = content.GetProperty("hero").GetProperty("image");
        Assert.StartsWith("media/pages/", image.GetProperty("key").GetString());

        // Keep the image by sending its key; the public page returns it.
        var kept = await _admin.JsonAsync(HttpMethod.Put, "/api/v1/admin/cms/pages/home", new
        {
            version = saved.GetProperty("version").GetUInt32(),
            title = (string?)null,
            seoTitle = (string?)null,
            seoDescription = (string?)null,
            content = new
            {
                hero = new { title = "Domaći džemovi", subtitle = (string?)null, primaryCtaLabel = (string?)null, secondaryCtaLabel = (string?)null, image = new { key = image.GetProperty("key").GetString(), alt = "Tegle 2" } },
                intro = (object?)null, story = (object?)null, whyUs = (object?)null, quality = (object?)null,
            },
        });
        Assert.Equal(image.GetProperty("key").GetString(), kept.GetProperty("content").GetProperty("hero").GetProperty("image").GetProperty("key").GetString());
        var publicHome = await PublicAsync("/api/v1/content/pages/home");
        Assert.Equal("Tegle 2", publicHome.GetProperty("content").GetProperty("hero").GetProperty("image").GetProperty("alt").GetString());

        // Restore to empty; the image files are removed.
        await _admin.JsonAsync(HttpMethod.Put, "/api/v1/admin/cms/pages/home", new
        {
            version = kept.GetProperty("version").GetUInt32(), title = (string?)null, seoTitle = (string?)null, seoDescription = (string?)null, content = (object?)null,
        });
        Assert.Equal(HttpStatusCode.NotFound, await PublicStatusAsync("/api/v1/content/pages/home"));
        Assert.False(await _minio.ExistsAsync($"{image.GetProperty("key").GetString()}/manifest.json"));
    }

    [Fact]
    public async Task Faq_order_and_publish_flag()
    {
        var first = await _admin.JsonAsync(HttpMethod.Post, "/api/v1/admin/faq", new { question = $"Prvo {Unique.Token()}?", answer = "Da.", isPublished = true }, HttpStatusCode.Created);
        var second = await _admin.JsonAsync(HttpMethod.Post, "/api/v1/admin/faq", new { question = $"Drugo {Unique.Token()}?", answer = "Ne.", isPublished = true }, HttpStatusCode.Created);
        var hidden = await _admin.JsonAsync(HttpMethod.Post, "/api/v1/admin/faq", new { question = $"Skriveno {Unique.Token()}?", answer = "Možda.", isPublished = false }, HttpStatusCode.Created);

        var ids = (await _admin.GetAsync("/api/v1/admin/faq")).Items().Select(i => i.Id()).ToList();
        Assert.Equal([first.Id(), second.Id(), hidden.Id()], ids.TakeLast(3));

        var reordered = ids.Where(i => i != second.Id()).Prepend(second.Id()).ToList();
        await _admin.JsonAsync(HttpMethod.Put, "/api/v1/admin/faq/order", new { ids = reordered });
        var publicIds = (await PublicAsync("/api/v1/content/faq")).Items().Select(i => i.Id()).ToList();
        Assert.Equal(second.Id(), publicIds[0]);
        Assert.True(publicIds.IndexOf(second.Id()) < publicIds.IndexOf(first.Id()));
        Assert.DoesNotContain(hidden.Id(), publicIds);

        var incomplete = await _admin.SendAsync(HttpMethod.Put, "/api/v1/admin/faq/order", new { ids = new[] { first.Id() } });
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);

        foreach (var id in new[] { first.Id(), second.Id(), hidden.Id() })
        {
            await _admin.JsonAsync(HttpMethod.Delete, $"/api/v1/admin/faq/{id}", null, HttpStatusCode.NoContent);
        }

        Assert.DoesNotContain((await PublicAsync("/api/v1/content/faq")).Items(), i => i.Id() == first.Id());
    }

    [Fact]
    public async Task Testimonials_are_public_only_when_published()
    {
        var draft = await _admin.JsonAsync(HttpMethod.Post, "/api/v1/admin/testimonials",
            new { authorName = "Marija", place = "Kraljevo", text = "Odličan džem.", isPublished = false, sortOrder = 0 }, HttpStatusCode.Created);
        Assert.DoesNotContain((await PublicAsync("/api/v1/content/testimonials")).Items(), t => t.Id() == draft.Id());

        await _admin.JsonAsync(HttpMethod.Put, $"/api/v1/admin/testimonials/{draft.Id()}",
            new { authorName = "Marija", place = "Kraljevo", text = "Odličan džem.", isPublished = true, sortOrder = 0 });
        Assert.Contains((await PublicAsync("/api/v1/content/testimonials")).Items(), t => t.Id() == draft.Id());
        await _admin.JsonAsync(HttpMethod.Delete, $"/api/v1/admin/testimonials/{draft.Id()}", null, HttpStatusCode.NoContent);
        Assert.DoesNotContain((await PublicAsync("/api/v1/content/testimonials")).Items(), t => t.Id() == draft.Id());
    }
}
