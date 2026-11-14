using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Zladovce.Api.RateLimiting;
using Zladovce.Application.Common;
using Zladovce.Application.Notifications;
using Zladovce.Application.Tracking;
using Zladovce.Domain.Notifications;

namespace Zladovce.Api.Endpoints;

/// <summary>Public order tracking (no authentication).</summary>
public static class TrackingEndpoints
{
    public static void MapTrackingEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/tracking/lookup", async (TrackingLookupRequest request, ITrackingService service, CancellationToken ct) =>
                TypedResults.Ok(await service.LookupAsync(request.Code, ct)))
            .WithTags("Tracking")
            .AllowAnonymous()
            .RequirePostgresRateLimit(RateLimitPolicies.TrackingLookup)
            .RequireGlobalPostgresRateLimit(RateLimitPolicies.TrackingLookupGlobal)
            .WithName("LookupTracking")
            .WithSummary("Status, timeline and courier data of the order with this tracking code.")
            .WithDescription(
                "Body `{ \"code\": \"…\" }`. The code has 26 Crockford Base32 characters, shown as `XXXXX-XXXXX-XXXXX-XXXXX-XXXXXX`; " +
                "hyphens and spaces are optional, case is ignored, `O` counts as `0` and `I`/`L` as `1`. The page takes it from the " +
                "email link `/track-order#kod=<code>` (URL fragment) and sends it here in the body — never in a URL. The response has no " +
                "name, address, phone, email, items or internal notes. Errors: 404 TRACKING_NOT_FOUND — the same body for unknown, " +
                "revoked and malformed codes; 400 VALIDATION_FAILED only when `code` is missing from the body; " +
                "429 RATE_LIMITED (10 per 10 minutes per IP, plus a global cap; `Retry-After` in seconds).")
            .Problems(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status429TooManyRequests);
    }
}

/// <summary>Email outbox administration and the order's tracking codes (Owner and Staff).</summary>
public static class AdminEmailEndpoints
{
    public static void MapAdminEmailEndpoints(this IEndpointRouteBuilder admin)
    {
        var emails = admin.MapGroup("/emails").WithTags("Admin – emails");

        emails.MapGet("", async ([AsParameters] AdminEmailListQuery query, IAdminEmailService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListAsync(query.ToCriteria(), ct)))
            .WithName("ListAdminEmails")
            .WithSummary("Outbox emails, newest first: state, attempts and last error. No bodies.")
            .WithDescription(
                "`status`: Pending, Processing, Sent, Dead (several comma-separated and/or repeated). `orderId`: one order. Paging " +
                "`page`, `pageSize`. Errors: 400 VALIDATION_FAILED.")
            .Problems(StatusCodes.Status400BadRequest);

        emails.MapGet("/{id:guid}", async (Guid id, IAdminEmailService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetAsync(id, ct)))
            .WithName("GetAdminEmail")
            .WithSummary("One outbox email with every send attempt (`attemptLog`).")
            .WithDescription("Errors: 404 NOT_FOUND.")
            .Problems(StatusCodes.Status404NotFound);

        emails.MapPost("/{id:guid}/retry", async (Guid id, IAdminEmailService service, CancellationToken ct) =>
                TypedResults.Ok(await service.RetryAsync(id, ct)))
            .WithName("RetryAdminEmail")
            .WithSummary("Send a Dead email again (\"Pošalji ponovo\"): Pending, due now, attempts start over.")
            .WithDescription(
                "When the email's tracking code is no longer stored, a new code is issued for the order. Errors: 404 NOT_FOUND; " +
                "409 EMAIL_NOT_RETRYABLE (the email is not Dead).")
            .Problems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);

        var orders = admin.MapGroup("/orders").WithTags("Admin – orders");

        orders.MapPost("/{id:guid}/emails/resend-confirmation", async (Guid id, IAdminEmailService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ResendConfirmationAsync(id, ct)))
            .WithName("ResendAdminOrderConfirmation")
            .WithSummary("Queue the order confirmation again with a new tracking code. Returns the queued email.")
            .WithDescription(
                "Earlier codes stay valid (use `tracking/revoke` first to invalidate them). Allowed in every status. " +
                "Errors: 404 NOT_FOUND.")
            .Problems(StatusCodes.Status404NotFound);

        orders.MapPost("/{id:guid}/tracking/revoke", async (Guid id, ITrackingService service, CancellationToken ct) =>
                TypedResults.Ok(await service.RevokeAllAsync(id, ct)))
            .WithName("RevokeAdminOrderTracking")
            .WithSummary("Revoke every tracking code of the order issued so far, including codes in emails not yet sent.")
            .WithDescription(
                "Revoked codes give 404 TRACKING_NOT_FOUND. Codes issued later (next status email, resent confirmation) work. " +
                "Errors: 404 NOT_FOUND.")
            .Problems(StatusCodes.Status404NotFound);
    }
}

/// <summary>Query of <c>GET /api/v1/admin/emails</c>.</summary>
public sealed class AdminEmailListQuery
{
    /// <summary><c>Pending</c>, <c>Processing</c>, <c>Sent</c>, <c>Dead</c>; several comma-separated and/or repeated; omitted = all.</summary>
    [FromQuery(Name = "status")]
    public string[]? Status { get; set; }

    /// <summary>Only the emails of this order.</summary>
    [FromQuery(Name = "orderId")]
    public Guid? OrderId { get; set; }

    [FromQuery(Name = "page")]
    public int? Page { get; set; }

    [FromQuery(Name = "pageSize")]
    public int? PageSize { get; set; }

    public IEnumerable<string> StatusValuesRequested() =>
        (Status ?? []).SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public AdminEmailListCriteria ToCriteria() => new(
        StatusValuesRequested().Select(Enum.Parse<EmailOutboxStatus>).ToHashSet(),
        OrderId,
        PageRequest.From(Page, PageSize));
}

public sealed class AdminEmailListQueryValidator : AbstractValidator<AdminEmailListQuery>
{
    private static readonly string[] Statuses = Enum.GetNames<EmailOutboxStatus>();

    public AdminEmailListQueryValidator()
    {
        RuleFor(q => q.StatusValuesRequested())
            .Must(values => values.All(value => Statuses.Contains(value, StringComparer.Ordinal)))
            .OverridePropertyName("status")
            .WithMessage($"Dozvoljene vrednosti: {string.Join(", ", Statuses)}.");
        this.PagingRules(q => q.Page, q => q.PageSize);
    }
}
