using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zladovce.Application.Media;
using Zladovce.Application.Common;
using Zladovce.Application.Common.Errors;
using Zladovce.Domain.StoreSettings;

namespace Zladovce.Application.StoreSettings;

public interface IStoreSettingsService
{
    Task<PublicStoreResponse> GetPublicAsync(CancellationToken cancellationToken);

    Task<AdminStoreSettingsResponse> GetAdminAsync(CancellationToken cancellationToken);

    /// <summary>Replaces every editable value (Owner). Audited by the audit interceptor as <c>StoreSettings.Updated</c>.</summary>
    Task<AdminStoreSettingsResponse> UpdateAsync(UpdateStoreSettingsRequest request, CancellationToken cancellationToken);
}

public sealed class StoreSettingsService(
    IZladovceDbContext db,
    IRequestContext requestContext,
    IImageProcessor images,
    IOptions<MediaOptions> mediaOptions,
    TimeProvider timeProvider) : IStoreSettingsService
{
    public async Task<PublicStoreResponse> GetPublicAsync(CancellationToken cancellationToken) =>
        PublicStoreResponse.From(await LoadAsync(tracked: false, cancellationToken), mediaOptions.Value);

    public async Task<AdminStoreSettingsResponse> GetAdminAsync(CancellationToken cancellationToken) =>
        await ToAdminAsync(await LoadAsync(tracked: false, cancellationToken), cancellationToken);

    public async Task<AdminStoreSettingsResponse> UpdateAsync(UpdateStoreSettingsRequest request, CancellationToken cancellationToken)
    {
        var admin = requestContext.Admin ?? throw new ForbiddenException();
        var settings = await LoadAsync(tracked: true, cancellationToken);
        if (settings.Version != request.Version!.Value)
        {
            throw new ConflictException(ErrorTexts.ConcurrentEditDetail, ErrorCodes.ConcurrentEdit, ErrorTexts.ConcurrentEditTitle);
        }

        var now = timeProvider.GetUtcNow();
        settings.Update(request.ToValues(), admin.Id, now);
        var replaced = await ApplyLogoAsync(settings, request.LogoKey, admin.Id, now, cancellationToken);
        db.Entry(settings).Property(s => s.Version).OriginalValue = request.Version.Value;
        await db.SaveChangesAsync(cancellationToken);
        if (replaced is { } old)
        {
            await images.DeleteRenditionsAsync(old.Key, old.Width, cancellationToken);
        }

        db.ChangeTracker.Clear();
        return await GetAdminAsync(cancellationToken);
    }

    /// <summary>
    /// <c>logoKey</c>: the current key keeps the logo, null removes it, an upload key (purpose <c>logo</c>) replaces it. Returns the
    /// logo that was replaced or removed, whose files are deleted after the save.
    /// </summary>
    private async Task<(string Key, int Width)?> ApplyLogoAsync(
        StoreSettingsRecord settings, string? logoKey, Guid adminId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        (string Key, int Width)? previous = settings is { LogoKey: { } key, LogoWidth: { } width } ? (key, width) : null;
        if (string.IsNullOrWhiteSpace(logoKey))
        {
            settings.RemoveLogo(adminId, now);
            return previous;
        }

        if (logoKey == settings.LogoKey)
        {
            return null;
        }

        var image = await images.GetProcessedAsync(logoKey.Trim(), MediaPurpose.Logo, "logoKey", cancellationToken);
        settings.SetLogo(image.StorageKey, image.Width, image.Height, adminId, now);
        return previous;
    }

    private async Task<StoreSettingsRecord> LoadAsync(bool tracked, CancellationToken cancellationToken)
    {
        var query = tracked ? db.StoreSettings : db.StoreSettings.AsNoTracking();

        // The row is created by the first migration; its absence is a deployment fault, not a client error.
        return await query.SingleOrDefaultAsync(s => s.Id == StoreSettingsRecord.SingletonId, cancellationToken)
            ?? throw new InvalidOperationException("The store_settings row is missing. Apply the database migrations.");
    }

    private async Task<AdminStoreSettingsResponse> ToAdminAsync(StoreSettingsRecord s, CancellationToken cancellationToken)
    {
        var updatedBy = s.UpdatedByAdminId is { } adminId
            ? await db.AdminUsers.AsNoTracking().Where(a => a.Id == adminId).Select(a => a.FullName).SingleOrDefaultAsync(cancellationToken)
            : null;

        return new AdminStoreSettingsResponse(
            s.StoreName,
            s.LogoKey,
            StoreLogo.From(mediaOptions.Value, s),
            s.ContactEmail,
            s.Phone,
            s.BusinessAddress,
            s.ShippingEnabled,
            s.ShippingPrice,
            s.FreeShippingThreshold,
            s.MinOrderAmount,
            s.EstimatedDeliveryText,
            s.DeliveryInfo,
            s.FacebookUrl,
            s.InstagramUrl,
            [.. s.OtherLinks.Select(l => new StoreLinkDto { Label = l.Label, Url = l.Url })],
            s.DefaultSeoTitle,
            s.DefaultSeoDescription,
            s.UpdatedAt,
            s.UpdatedByAdminId,
            updatedBy,
            s.Version);
    }
}
