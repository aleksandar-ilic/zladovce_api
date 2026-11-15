using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Zladovce.Domain.Catalog;
using Zladovce.Domain.Categories;
using Zladovce.Domain.Content;
using Zladovce.Domain.Site;
using Zladovce.Domain.StoreSettings;
using Zladovce.Infrastructure.Persistence;

namespace Zladovce.Infrastructure.Site;

/// <summary>Configuration section <c>Site</c>: the storefront rebuild webhook (A14).</summary>
public sealed class SiteOptions
{
    public const string SectionName = "Site";

    /// <summary>
    /// Build hook of the storefront hosting, called with an empty POST. It is a secret (the URL usually carries the token): keep it
    /// out of files; on AWS prefer <see cref="RebuildWebhookUrlSecretArn"/>. Empty = no rebuild calls (changes stay pending).
    /// Must be https; plain http is accepted only for loopback addresses (tests, local tools).
    /// </summary>
    public string? RebuildWebhookUrl { get; set; }

    /// <summary>Secrets Manager secret holding the webhook URL (secret string = the URL, or JSON <c>{ "url": "…" }</c>).</summary>
    public string? RebuildWebhookUrlSecretArn { get; set; }

    /// <summary>At most one call per this many seconds (default 300 = 5 minutes); changes in between are coalesced.</summary>
    public int RebuildMinIntervalSeconds { get; set; } = 300;

    /// <summary>Timeout of the webhook call (default 10 s).</summary>
    public int RebuildTimeoutSeconds { get; set; } = 10;

    public static bool IsAcceptableUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
}

/// <summary>
/// Marks the storefront for a rebuild whenever something it prerenders changes: categories, products, variants, product images,
/// store settings, blog categories and posts, CMS pages, FAQ and testimonials. Runs after the save, inside the caller's
/// transaction when there is one (so a rolled-back change requests nothing); without one, right after the commit. Stock changes
/// do not count (stock and prices are loaded live in the browser).
/// </summary>
public sealed class SiteRebuildInterceptor(TimeProvider timeProvider, ILogger<SiteRebuildInterceptor>? logger = null) : SaveChangesInterceptor
{
    public static readonly IReadOnlySet<Type> PrerenderedEntities = new HashSet<Type>
    {
        typeof(Category), typeof(Product), typeof(ProductVariant), typeof(ProductImage), typeof(StoreSettingsRecord),
        typeof(BlogCategory), typeof(BlogPost), typeof(CmsPage), typeof(FaqItem), typeof(Testimonial),
    };

    private static readonly ConditionalWeakTable<DbContext, object> Marked = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Inspect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Inspect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is { } context && Marked.TryGetValue(context, out _))
        {
            Marked.Remove(context);
            context.Database.ExecuteSql(Sql(timeProvider.GetUtcNow()));
        }

        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && Marked.TryGetValue(context, out _))
        {
            Marked.Remove(context);
            try
            {
                await context.Database.ExecuteSqlAsync(Sql(timeProvider.GetUtcNow()), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && context.Database.CurrentTransaction is null)
            {
                // The change itself is committed; a missed rebuild request is logged, the next content change requests it again.
                logger?.LogWarning("Requesting a storefront rebuild failed: {ExceptionType}.", exception.GetType().Name);
            }
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } context)
        {
            Marked.Remove(context);
        }

        base.SaveChangesFailed(eventData);
    }

    private static FormattableString Sql(DateTimeOffset now) =>
        $"UPDATE site_rebuild_requests SET pending = true, requested_at = {now} WHERE id = 1";

    private static void Inspect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var changed = context.ChangeTracker.Entries().Any(e =>
            e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && PrerenderedEntities.Contains(e.Metadata.ClrType));
        if (changed)
        {
            Marked.AddOrUpdate(context, true);
        }
    }
}

/// <summary>Result of one rebuild check.</summary>
/// <param name="Outcome"><c>NotConfigured</c>, <c>Idle</c> (nothing pending), <c>Waiting</c> (pending, interval not over), <c>Triggered</c>, <c>Failed</c>.</param>
public sealed record SiteRebuildResult(string Outcome, int? StatusCode = null);

public interface ISiteRebuildDispatcher
{
    Task<SiteRebuildResult> RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Called by the outbox worker on every run (Lambda every minute; locally the in-process dispatcher). Also notices scheduled
/// blog posts whose publication time has passed since the last check. When a rebuild is pending and the last call is at least
/// <see cref="SiteOptions.RebuildMinIntervalSeconds"/> old, the row is claimed with one conditional update (so concurrent workers
/// call once) and the webhook gets an empty POST. A failed call puts the request back to pending.
/// </summary>
public sealed class SiteRebuildDispatcher(
    NpgsqlDataSource dataSource,
    ZladovceDbContext db,
    IOptions<SiteOptions> options,
    TimeProvider timeProvider,
    ILogger<SiteRebuildDispatcher> logger,
    HttpMessageHandler? handler = null) : ISiteRebuildDispatcher
{
    private static readonly SemaphoreSlim SecretGate = new(1, 1);
    private static string? _secretUrl;

    public async Task<SiteRebuildResult> RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        await NoticeScheduledPostsAsync(now, cancellationToken);

        var url = await WebhookUrlAsync(settings, cancellationToken);
        if (url is null)
        {
            return new SiteRebuildResult("NotConfigured");
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.RebuildMinIntervalSeconds));
        await using (var claim = dataSource.CreateCommand(
            """
            UPDATE site_rebuild_requests SET pending = false, last_triggered_at = $1
            WHERE id = 1 AND pending AND (last_triggered_at IS NULL OR last_triggered_at <= $2)
            """))
        {
            claim.Parameters.Add(new NpgsqlParameter { Value = now });
            claim.Parameters.Add(new NpgsqlParameter { Value = now - interval });
            if (await claim.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                var pending = await db.SiteRebuildRequests.AsNoTracking().AnyAsync(r => r.Id == SiteRebuildRequest.SingletonId && r.Pending, cancellationToken);
                return new SiteRebuildResult(pending ? "Waiting" : "Idle");
            }
        }

        string result;
        int? status = null;
        try
        {
            using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.RebuildTimeoutSeconds, 1, 60)));
            using var response = await client.PostAsync(url, content: null, timeout.Token);
            status = (int)response.StatusCode;
            result = $"HTTP {status}";
            if (!response.IsSuccessStatusCode)
            {
                await RecordAsync(result, failed: true, cancellationToken);
                logger.LogWarning("Storefront rebuild webhook answered {StatusCode}; the request stays pending.", status);
                return new SiteRebuildResult("Failed", status);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The URL is a secret and is never logged.
            result = exception.GetType().Name;
            await RecordAsync(result, failed: true, CancellationToken.None);
            logger.LogWarning("Storefront rebuild webhook failed: {ExceptionType}; the request stays pending.", exception.GetType().Name);
            return new SiteRebuildResult("Failed");
        }

        await RecordAsync(result, failed: false, cancellationToken);
        logger.LogInformation("Storefront rebuild requested ({Result}).", result);
        return new SiteRebuildResult("Triggered", status);
    }

    private async Task NoticeScheduledPostsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var row = await db.SiteRebuildRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == SiteRebuildRequest.SingletonId, cancellationToken);
        if (row is null)
        {
            return;
        }

        var since = row.ScheduledCheckedUntil ?? now;
        var becamePublic = await db.BlogPosts.AsNoTracking().AnyAsync(
            p => p.Status == BlogPostStatus.Published && p.DeletedAt == null && p.PublishedAt > since && p.PublishedAt <= now, cancellationToken);

        await using var command = dataSource.CreateCommand(
            "UPDATE site_rebuild_requests SET scheduled_checked_until = $1, pending = pending OR $2, requested_at = CASE WHEN $2 THEN $1 ELSE requested_at END WHERE id = 1");
        command.Parameters.Add(new NpgsqlParameter { Value = now });
        command.Parameters.Add(new NpgsqlParameter { Value = becamePublic });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RecordAsync(string result, bool failed, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            failed
                ? "UPDATE site_rebuild_requests SET pending = true, last_result = $1 WHERE id = 1"
                : "UPDATE site_rebuild_requests SET last_result = $1 WHERE id = 1");
        command.Parameters.Add(new NpgsqlParameter { Value = result });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<string?> WebhookUrlAsync(SiteOptions settings, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(settings.RebuildWebhookUrlSecretArn))
        {
            if (_secretUrl is null)
            {
                await SecretGate.WaitAsync(cancellationToken);
                try
                {
                    if (_secretUrl is null)
                    {
                        using var client = new AmazonSecretsManagerClient();
                        var secret = await client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = settings.RebuildWebhookUrlSecretArn }, cancellationToken);
                        _secretUrl = ParseSecret(secret.SecretString);
                    }
                }
                finally
                {
                    SecretGate.Release();
                }
            }

            return SiteOptions.IsAcceptableUrl(_secretUrl) ? _secretUrl : null;
        }

        var url = settings.RebuildWebhookUrl?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        if (!SiteOptions.IsAcceptableUrl(url))
        {
            logger.LogWarning("Site:RebuildWebhookUrl is not an https URL; no rebuild is requested.");
            return null;
        }

        return url;
    }

    public static string? ParseSecret(string? secret)
    {
        var value = secret?.Trim();
        if (value is not null && value.StartsWith('{'))
        {
            using var document = JsonDocument.Parse(value);
            value = document.RootElement.TryGetProperty("url", out var url) ? url.GetString() : null;
        }

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
