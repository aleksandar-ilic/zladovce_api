using Microsoft.AspNetCore.Http.HttpResults;
using Zladovce.Api.Auth;
using Zladovce.Api.RateLimiting;
using Zladovce.Application.Common;
using Zladovce.Application.Content;
using Zladovce.Application.Marketing;
using Zladovce.Domain.Content;
using Zladovce.Domain.Marketing;

namespace Zladovce.Api.Endpoints;

/// <summary>Public blog, CMS pages, FAQ, testimonials, newsletter and contact form (no authentication).</summary>
public static class PublicContentEndpoints
{
    private static readonly CmsPageKey[] RichTextKeys = [CmsPageKey.Privacy, CmsPageKey.Terms, CmsPageKey.Delivery];

    public static void MapPublicContentEndpoints(this IEndpointRouteBuilder api)
    {
        var blog = api.MapGroup("/blog").WithTags("Blog").AllowAnonymous();

        blog.MapGet("/categories", async (IPublicBlogService service, CancellationToken ct) => TypedResults.Ok(await service.ListCategoriesAsync(ct)))
            .WithName("ListBlogCategories")
            .WithSummary("Blog categories by sort order, with the number of public posts (0 included).");

        blog.MapGet("/posts", async ([AsParameters] PublicBlogPostListQuery query, IPublicBlogService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListPostsAsync(query.Category, PageRequest.From(query.Page, query.PageSize), ct)))
            .WithName("ListBlogPosts")
            .WithSummary("Public posts (Published, publication time reached, not archived), newest first.")
            .WithDescription("`category`: blog category slug (unknown → 404). Paging `page`, `pageSize`. Errors: 400 VALIDATION_FAILED; 404 NOT_FOUND.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);

        blog.MapGet("/posts/{slug}", async (string slug, IPublicBlogService service, CancellationToken ct) => TypedResults.Ok(await service.GetPostAsync(slug, ct)))
            .WithName("GetBlogPost")
            .WithSummary("One public post with its sanitised HTML content.")
            .WithDescription("Drafts, scheduled posts before their time and archived posts answer 404 NOT_FOUND.")
            .Problems(StatusCodes.Status404NotFound);

        blog.MapGet("/latest-posts", async ([AsParameters] LimitQuery query, IPublicBlogService service, CancellationToken ct) =>
                TypedResults.Ok(await service.LatestAsync(query.Limit ?? 3, ct)))
            .WithName("ListLatestBlogPosts")
            .WithSummary("Newest public posts for the homepage previews (`limit` 1–12, default 3). Empty when there are none.")
            .Problems(StatusCodes.Status400BadRequest);

        var content = api.MapGroup("/content").WithTags("Content").AllowAnonymous();

        content.MapGet("/pages/home", async (ICmsService service, CancellationToken ct) => TypedResults.Ok(await service.GetHomeAsync(ct)))
            .WithName("GetHomePage")
            .WithSummary("Homepage texts (hero, intro, family story, why us, quality). 404 while the owner has not written them.")
            .Problems(StatusCodes.Status404NotFound);

        content.MapGet("/pages/about", async (ICmsService service, CancellationToken ct) => TypedResults.Ok(await service.GetAboutAsync(ct)))
            .WithName("GetAboutPage")
            .WithSummary("O nama: sanitised HTML and images. 404 while empty.")
            .Problems(StatusCodes.Status404NotFound);

        foreach (var key in RichTextKeys)
        {
            var name = CmsPageKeys.ToKey(key);
            content.MapGet($"/pages/{name}", async (ICmsService service, CancellationToken ct) => TypedResults.Ok(await service.GetRichTextAsync(key, ct)))
                .WithName($"Get{key}Page")
                .WithSummary($"Page `{name}`: title and sanitised HTML. 404 while empty (legal texts are never invented).")
                .Problems(StatusCodes.Status404NotFound);
        }

        content.MapGet("/faq", async (IFaqService service, CancellationToken ct) => TypedResults.Ok(await service.PublicAsync(ct)))
            .WithName("ListFaq")
            .WithSummary("Published FAQ items in the owner's order (answers are plain text).");

        content.MapGet("/testimonials", async (ITestimonialService service, CancellationToken ct) => TypedResults.Ok(await service.PublicAsync(ct)))
            .WithName("ListTestimonials")
            .WithSummary("Published real testimonials. Empty unless the owner entered some: then hide the homepage section (A13).");

        var newsletter = api.MapGroup("/newsletter").WithTags("Newsletter").AllowAnonymous();

        newsletter.MapPost("/subscriptions", async Task<Accepted<FormAcceptedResponse>> (NewsletterSubscribeRequest request, INewsletterService service, CancellationToken ct) =>
                TypedResults.Accepted((string?)null, await service.SubscribeAsync(request, ct)))
            .RequirePostgresRateLimit(RateLimitPolicies.NewsletterSignup)
            .WithName("SubscribeNewsletter")
            .WithSummary("Sign up for the newsletter (double opt-in). Always 202 with the same message.")
            .WithDescription(
                "Body `{ email, consent: true, website }` (`website` is a hidden honeypot: leave it empty; when filled nothing happens). " +
                "New and unsubscribed addresses get a confirmation email (`NewsletterConfirm`) with links `{shop}/newsletter/potvrda#token=…` " +
                "and `{shop}/newsletter/odjava#token=…`; pending addresses get it again at most every 30 minutes; confirmed ones nothing. " +
                "The response never tells which happened. Errors: 400 VALIDATION_FAILED (`email`, `consent`); 429 RATE_LIMITED (3 per 10 " +
                "minutes per IP, `Retry-After`).")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status429TooManyRequests);

        newsletter.MapPost("/confirm", async (NewsletterTokenRequest request, INewsletterService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ConfirmAsync(request.Token!, ct)))
            .RequirePostgresRateLimit(RateLimitPolicies.NewsletterToken)
            .WithName("ConfirmNewsletter")
            .WithSummary("Confirm the address with the token from the confirmation link (fragment `#token=`). Repeating it is fine.")
            .WithDescription("Errors: 400 NEWSLETTER_TOKEN_INVALID (malformed, forged, older than 7 days, superseded by a newer link, or unsubscribed); 400 VALIDATION_FAILED; 429 RATE_LIMITED.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status429TooManyRequests);

        newsletter.MapPost("/unsubscribe", async (NewsletterTokenRequest request, INewsletterService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UnsubscribeAsync(request.Token!, ct)))
            .RequirePostgresRateLimit(RateLimitPolicies.NewsletterToken)
            .WithName("UnsubscribeNewsletter")
            .WithSummary("One-click unsubscribe with the token from any newsletter email (`#token=` of the odjava link). Idempotent.")
            .WithDescription("Errors: 400 NEWSLETTER_TOKEN_INVALID; 400 VALIDATION_FAILED; 429 RATE_LIMITED.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status429TooManyRequests);

        api.MapPost("/contact", async Task<Accepted<FormAcceptedResponse>> (ContactRequest request, IContactService service, CancellationToken ct) =>
                TypedResults.Accepted((string?)null, await service.SendAsync(request, ct)))
            .WithTags("Contact")
            .AllowAnonymous()
            .RequirePostgresRateLimit(RateLimitPolicies.ContactForm)
            .WithName("SendContactMessage")
            .WithSummary("Contact form: stores the message and emails it to the store contact address (Reply-To = the sender).")
            .WithDescription(
                "Body `{ name, email, message, website }` (`website` = hidden honeypot; when filled the answer is the same 202 and nothing " +
                "is stored). Errors: 400 VALIDATION_FAILED (`name`, `email`, `message`); 409 CONTACT_NOT_CONFIGURED (the store has no " +
                "contact email: show the phone/address instead); 429 RATE_LIMITED (3 per 10 minutes per IP).")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict, StatusCodes.Status429TooManyRequests);
    }
}

/// <summary>Blog, CMS, FAQ, testimonials, contact messages and newsletter administration.</summary>
public static class AdminContentEndpoints
{
    private static readonly CmsPageKey[] RichTextKeys = [CmsPageKey.Privacy, CmsPageKey.Terms, CmsPageKey.Delivery];

    public static void MapAdminContentEndpoints(this IEndpointRouteBuilder admin)
    {
        MapBlog(admin);
        MapCms(admin);
        MapFaqAndTestimonials(admin);
        MapMessages(admin);
    }

    private static void MapBlog(IEndpointRouteBuilder admin)
    {
        var categories = admin.MapGroup("/blog/categories").WithTags("Admin – blog");

        categories.MapGet("", async (IBlogAdminService service, CancellationToken ct) => TypedResults.Ok(await service.ListCategoriesAsync(ct)))
            .WithName("ListAdminBlogCategories")
            .WithSummary("Blog categories with post counts.");

        categories.MapPost("", async Task<Created<AdminBlogCategoryResponse>> (SaveBlogCategoryRequest request, IBlogAdminService service, CancellationToken ct) =>
            {
                var created = await service.CreateCategoryAsync(request, ct);
                return TypedResults.Created($"{EndpointMapping.ApiBasePath}/admin/blog/categories/{created.Id}", created);
            })
            .WithName("CreateAdminBlogCategory")
            .WithSummary("Add a blog category.")
            .WithDescription("Errors: 400 VALIDATION_FAILED; 409 SLUG_TAKEN (`errors.slug`).")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict);

        categories.MapPut("/{id:guid}", async (Guid id, SaveBlogCategoryRequest request, IBlogAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateCategoryAsync(id, request, ct)))
            .WithName("UpdateAdminBlogCategory")
            .WithSummary("Change a blog category.")
            .WithDescription("Errors: 400 VALIDATION_FAILED; 404 NOT_FOUND; 409 SLUG_TAKEN.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        categories.MapDelete("/{id:guid}", async Task<NoContent> (Guid id, IBlogAdminService service, CancellationToken ct) =>
            {
                await service.DeleteCategoryAsync(id, ct);
                return TypedResults.NoContent();
            })
            .WithName("DeleteAdminBlogCategory")
            .WithSummary("Delete a blog category that no post (draft or published) uses.")
            .WithDescription("Archived posts lose the link. Errors: 404 NOT_FOUND; 409 BLOG_CATEGORY_HAS_POSTS.")
            .Problems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        var posts = admin.MapGroup("/blog/posts").WithTags("Admin – blog");

        posts.MapGet("", async ([AsParameters] AdminBlogPostListQuery query, IBlogAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListPostsAsync(query.ToCriteria(), ct)))
            .WithName("ListAdminBlogPosts")
            .WithSummary("Posts (drafts included, archived excluded), last changed first.")
            .WithDescription("`q` search, `status` Draft|Scheduled|Published, `categoryId`, paging. Errors: 400 VALIDATION_FAILED.")
            .Problems(StatusCodes.Status400BadRequest);

        posts.MapGet("/{id:guid}", async (Guid id, IBlogAdminService service, CancellationToken ct) => TypedResults.Ok(await service.GetPostAsync(id, ct)))
            .WithName("GetAdminBlogPost")
            .WithSummary("A post with its content and `version`.")
            .WithDescription("Errors: 404 NOT_FOUND.")
            .Problems(StatusCodes.Status404NotFound);

        posts.MapPost("", async Task<Created<AdminBlogPostResponse>> (SaveBlogPostRequest request, IBlogAdminService service, CancellationToken ct) =>
            {
                var created = await service.CreatePostAsync(request, ct);
                return TypedResults.Created($"{EndpointMapping.ApiBasePath}/admin/blog/posts/{created.Id}", created);
            })
            .WithName("CreateAdminBlogPost")
            .WithSummary("Create a post as Draft. `contentHtml` is sanitised (the stored value is returned).")
            .WithDescription(
                "Errors: 400 VALIDATION_FAILED (`title`, `slug`, `excerpt`, `contentHtml`, `categoryId`, `coverImageKey`), INVALID_IMAGE, " +
                "UPLOAD_NOT_FOUND; 409 SLUG_TAKEN, IMAGE_NOT_READY, IMAGE_ALREADY_ATTACHED.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict);

        posts.MapPut("/{id:guid}", async (Guid id, SaveBlogPostRequest request, IBlogAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdatePostAsync(id, request, ct)))
            .WithName("UpdateAdminBlogPost")
            .WithSummary("Change a post (`version` required). A published post stays published and needs an excerpt and content.")
            .WithDescription("Errors: 400 VALIDATION_FAILED; 404 NOT_FOUND; 409 CONCURRENT_EDIT, SLUG_TAKEN, IMAGE_NOT_READY, IMAGE_ALREADY_ATTACHED.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        posts.MapPost("/{id:guid}/publish", async (Guid id, PublishBlogPostRequest request, IBlogAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.PublishAsync(id, request, ct)))
            .WithName("PublishAdminBlogPost")
            .WithSummary("Publish now (`publishedAt` null) or at a future time (state Scheduled until then).")
            .WithDescription("Needs an excerpt and content (400 VALIDATION_FAILED `excerpt`/`contentHtml`). Errors: 404 NOT_FOUND; 409 CONCURRENT_EDIT.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        posts.MapPost("/{id:guid}/unpublish", async (Guid id, UnpublishBlogPostRequest request, IBlogAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UnpublishAsync(id, request, ct)))
            .WithName("UnpublishAdminBlogPost")
            .WithSummary("Back to Draft (no longer public; `publishedAt` cleared).")
            .WithDescription("Errors: 404 NOT_FOUND; 409 CONCURRENT_EDIT.")
            .Problems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        posts.MapDelete("/{id:guid}", async Task<NoContent> (Guid id, IBlogAdminService service, CancellationToken ct) =>
            {
                await service.ArchivePostAsync(id, ct);
                return TypedResults.NoContent();
            })
            .WithName("ArchiveAdminBlogPost")
            .WithSummary("Archive a post (soft delete): never public again, slug freed, cover image files deleted.")
            .WithDescription("Errors: 404 NOT_FOUND.")
            .Problems(StatusCodes.Status404NotFound);

        admin.MapPost("/blog/images", async (BlogImageRequest request, IBlogAdminService service, CancellationToken ct) =>
                TypedResults.Ok(await service.AddImageAsync(request, ct)))
            .WithTags("Admin – blog")
            .WithName("AddAdminBlogImage")
            .WithSummary("Process an uploaded image (purpose `blog`) for use inside post content; returns its renditions.")
            .WithDescription(
                "Insert it as `<img src=\"{baseUrl}/800.webp\" alt=\"…\">` (any listed width); only images of this shop's media survive " +
                "sanitising. Errors: 400 VALIDATION_FAILED (`uploadKey`), INVALID_IMAGE, UPLOAD_NOT_FOUND; 409 IMAGE_NOT_READY.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict);
    }

    private static void MapCms(IEndpointRouteBuilder admin)
    {
        var pages = admin.MapGroup("/cms/pages").WithTags("Admin – CMS");

        pages.MapGet("", async (ICmsService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(ct)))
            .WithName("ListAdminCmsPages")
            .WithSummary("Every page key (home, about, privacy, terms, delivery) and whether it has content.");

        pages.MapGet("/home", async (ICmsService service, CancellationToken ct) => TypedResults.Ok(await service.GetHomeAdminAsync(ct)))
            .WithName("GetAdminHomePage")
            .WithSummary("Homepage content for editing (null content = empty).");

        pages.MapPut("/home", async (SaveHomePageRequest request, ICmsService service, CancellationToken ct) => TypedResults.Ok(await service.SaveHomeAsync(request, ct)))
            .WithName("SaveAdminHomePage")
            .WithSummary("Save the homepage. Images: `{ key, alt }` with the current key or an upload key (purpose `page`).")
            .WithDescription("Errors: 400 VALIDATION_FAILED (paths like `content.hero.title`, `content.whyUs.items[0].title`), INVALID_IMAGE; 409 CONCURRENT_EDIT, IMAGE_NOT_READY.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict);

        pages.MapGet("/about", async (ICmsService service, CancellationToken ct) => TypedResults.Ok(await service.GetAboutAdminAsync(ct)))
            .WithName("GetAdminAboutPage")
            .WithSummary("O nama for editing.");

        pages.MapPut("/about", async (SaveAboutPageRequest request, ICmsService service, CancellationToken ct) => TypedResults.Ok(await service.SaveAboutAsync(request, ct)))
            .WithName("SaveAdminAboutPage")
            .WithSummary("Save O nama: sanitised HTML and up to 6 images.")
            .WithDescription("Errors: 400 VALIDATION_FAILED (`content.bodyHtml`, `content.images[i].key`), INVALID_IMAGE; 409 CONCURRENT_EDIT, IMAGE_NOT_READY.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict);

        foreach (var key in RichTextKeys)
        {
            var name = CmsPageKeys.ToKey(key);
            pages.MapGet($"/{name}", async (ICmsService service, CancellationToken ct) => TypedResults.Ok(await service.GetRichTextAdminAsync(key, ct)))
                .WithName($"GetAdmin{key}Page")
                .WithSummary($"Page `{name}` for editing.");

            pages.MapPut($"/{name}", async (SaveRichTextPageRequest request, ICmsService service, CancellationToken ct) =>
                    TypedResults.Ok(await service.SaveRichTextAsync(key, request, ct)))
                .WithName($"SaveAdmin{key}Page")
                .WithSummary($"Save page `{name}` (title + sanitised HTML). An empty body makes the page empty (public 404).")
                .WithDescription("Errors: 400 VALIDATION_FAILED; 409 CONCURRENT_EDIT.")
                .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict);
        }
    }

    private static void MapFaqAndTestimonials(IEndpointRouteBuilder admin)
    {
        var faq = admin.MapGroup("/faq").WithTags("Admin – FAQ");

        faq.MapGet("", async (IFaqService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(ct)))
            .WithName("ListAdminFaq")
            .WithSummary("All FAQ items in order (published or not).");

        faq.MapPost("", async Task<Created<AdminFaqItemResponse>> (SaveFaqItemRequest request, IFaqService service, CancellationToken ct) =>
            {
                var created = await service.CreateAsync(request, ct);
                return TypedResults.Created($"{EndpointMapping.ApiBasePath}/admin/faq/{created.Id}", created);
            })
            .WithName("CreateAdminFaqItem")
            .WithSummary("Add a FAQ item at the end.")
            .WithDescription("Errors: 400 VALIDATION_FAILED.")
            .Problems(StatusCodes.Status400BadRequest);

        faq.MapPut("/order", async (ReorderFaqRequest request, IFaqService service, CancellationToken ct) => TypedResults.Ok(await service.ReorderAsync(request, ct)))
            .WithName("ReorderAdminFaq")
            .WithSummary("Set the order: every item id exactly once.")
            .WithDescription("Errors: 400 VALIDATION_FAILED (`ids`).")
            .Problems(StatusCodes.Status400BadRequest);

        faq.MapPut("/{id:guid}", async (Guid id, SaveFaqItemRequest request, IFaqService service, CancellationToken ct) => TypedResults.Ok(await service.UpdateAsync(id, request, ct)))
            .WithName("UpdateAdminFaqItem")
            .WithSummary("Change a FAQ item (also publish/unpublish with `isPublished`).")
            .WithDescription("Errors: 400 VALIDATION_FAILED; 404 NOT_FOUND.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);

        faq.MapDelete("/{id:guid}", async Task<NoContent> (Guid id, IFaqService service, CancellationToken ct) =>
            {
                await service.DeleteAsync(id, ct);
                return TypedResults.NoContent();
            })
            .WithName("DeleteAdminFaqItem")
            .WithSummary("Delete a FAQ item.")
            .WithDescription("Errors: 404 NOT_FOUND.")
            .Problems(StatusCodes.Status404NotFound);

        var testimonials = admin.MapGroup("/testimonials").WithTags("Admin – testimonials");

        testimonials.MapGet("", async (ITestimonialService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(ct)))
            .WithName("ListAdminTestimonials")
            .WithSummary("All testimonials.");

        testimonials.MapPost("", async Task<Created<AdminTestimonialResponse>> (SaveTestimonialRequest request, ITestimonialService service, CancellationToken ct) =>
            {
                var created = await service.CreateAsync(request, ct);
                return TypedResults.Created($"{EndpointMapping.ApiBasePath}/admin/testimonials/{created.Id}", created);
            })
            .WithName("CreateAdminTestimonial")
            .WithSummary("Add a real testimonial.")
            .WithDescription("Errors: 400 VALIDATION_FAILED.")
            .Problems(StatusCodes.Status400BadRequest);

        testimonials.MapPut("/{id:guid}", async (Guid id, SaveTestimonialRequest request, ITestimonialService service, CancellationToken ct) =>
                TypedResults.Ok(await service.UpdateAsync(id, request, ct)))
            .WithName("UpdateAdminTestimonial")
            .WithSummary("Change a testimonial.")
            .WithDescription("Errors: 400 VALIDATION_FAILED; 404 NOT_FOUND.")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);

        testimonials.MapDelete("/{id:guid}", async Task<NoContent> (Guid id, ITestimonialService service, CancellationToken ct) =>
            {
                await service.DeleteAsync(id, ct);
                return TypedResults.NoContent();
            })
            .WithName("DeleteAdminTestimonial")
            .WithSummary("Delete a testimonial.")
            .WithDescription("Errors: 404 NOT_FOUND.")
            .Problems(StatusCodes.Status404NotFound);
    }

    private static void MapMessages(IEndpointRouteBuilder admin)
    {
        var messages = admin.MapGroup("/contact-messages").WithTags("Admin – contact messages");

        messages.MapGet("", async ([AsParameters] ContactMessageListQuery query, IContactService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListAsync(query.UnreadOnly == true, PageRequest.From(query.Page, query.PageSize), ct)))
            .WithName("ListAdminContactMessages")
            .WithSummary("Contact-form messages, newest first, with the number of unread ones.")
            .Problems(StatusCodes.Status400BadRequest);

        messages.MapPost("/{id:guid}/read", async (Guid id, IContactService service, CancellationToken ct) => TypedResults.Ok(await service.SetReadAsync(id, true, ct)))
            .WithName("MarkAdminContactMessageRead")
            .WithSummary("Mark a message as read.")
            .Problems(StatusCodes.Status404NotFound);

        messages.MapPost("/{id:guid}/unread", async (Guid id, IContactService service, CancellationToken ct) => TypedResults.Ok(await service.SetReadAsync(id, false, ct)))
            .WithName("MarkAdminContactMessageUnread")
            .WithSummary("Mark a message as unread.")
            .Problems(StatusCodes.Status404NotFound);

        var subscribers = admin.MapGroup("/newsletter/subscribers").WithTags("Admin – newsletter").RequireAuthorization(AuthPolicies.OwnerOnly);

        subscribers.MapGet("", async ([AsParameters] SubscriberListQuery query, INewsletterService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListAsync(
                    query.Status is null ? null : Enum.Parse<NewsletterSubscriberStatus>(query.Status), query.Q, PageRequest.From(query.Page, query.PageSize), ct)))
            .WithName("ListAdminNewsletterSubscribers")
            .WithSummary("Newsletter addresses with their status (Owner). No campaigns are sent by the platform (A10).")
            .WithDescription("Errors: 400 VALIDATION_FAILED; 403 FORBIDDEN (Staff).")
            .Problems(StatusCodes.Status400BadRequest);

        subscribers.MapGet("/export", async (INewsletterService service, CancellationToken ct) =>
            {
                var csv = await service.ExportConfirmedAsync(ct);
                return TypedResults.File(csv.Content, CsvFile.ContentType, csv.FileName);
            })
            .WithName("ExportAdminNewsletterSubscribers")
            .WithSummary("CSV of confirmed subscribers (Owner): email, consent and confirmation time. Same CSV format as the reports.")
            .Produces(StatusCodes.Status200OK, typeof(string), "text/csv");
    }
}
