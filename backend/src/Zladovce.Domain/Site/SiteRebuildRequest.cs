namespace Zladovce.Domain.Site;

/// <summary>
/// The single row of <c>site_rebuild_requests</c> (<c>id = 1</c>): whether the prerendered storefront must be rebuilt. Content
/// changes set <see cref="Pending"/>; the worker calls the rebuild webhook at most once per minimum interval and clears it.
/// </summary>
public sealed class SiteRebuildRequest
{
    public const int SingletonId = 1;

    private SiteRebuildRequest()
    {
    }

    public int Id { get; private set; }

    /// <summary>A change happened after the last successful trigger.</summary>
    public bool Pending { get; private set; }

    /// <summary>Latest content change.</summary>
    public DateTimeOffset? RequestedAt { get; private set; }

    /// <summary>Last call of the webhook (successful or not).</summary>
    public DateTimeOffset? LastTriggeredAt { get; private set; }

    /// <summary>Scheduled blog posts that became public up to this moment were already taken into account.</summary>
    public DateTimeOffset? ScheduledCheckedUntil { get; private set; }

    /// <summary>HTTP status or error of the last call; no URL (it is a secret).</summary>
    public string? LastResult { get; private set; }
}
