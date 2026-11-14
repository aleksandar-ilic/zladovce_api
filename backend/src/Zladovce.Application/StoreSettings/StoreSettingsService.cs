using Microsoft.EntityFrameworkCore;
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

public sealed class StoreSettingsService(IZladovceDbContext db, IRequestContext requestContext, TimeProvider timeProvider) : IStoreSettingsService
{
    public async Task<PublicStoreResponse> GetPublicAsync(CancellationToken cancellationToken) =>
        PublicStoreResponse.From(await LoadAsync(tracked: false, cancellationToken));

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

        settings.Update(request.ToValues(), admin.Id, timeProvider.GetUtcNow());
        db.Entry(settings).Property(s => s.Version).OriginalValue = request.Version.Value;
        await db.SaveChangesAsync(cancellationToken);

        db.ChangeTracker.Clear();
        return await GetAdminAsync(cancellationToken);
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
