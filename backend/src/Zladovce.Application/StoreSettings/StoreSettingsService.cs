using Microsoft.EntityFrameworkCore;
using Zladovce.Application.Common;
using Zladovce.Domain.StoreSettings;

namespace Zladovce.Application.StoreSettings;

public interface IStoreSettingsService
{
    Task<PublicStoreResponse> GetPublicAsync(CancellationToken cancellationToken);
}

public sealed class StoreSettingsService(IZladovceDbContext db) : IStoreSettingsService
{
    public async Task<PublicStoreResponse> GetPublicAsync(CancellationToken cancellationToken)
    {
        // The row is created by the first migration; its absence is a deployment fault, not a client error.
        var settings = await db.StoreSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == StoreSettingsRecord.SingletonId, cancellationToken)
            ?? throw new InvalidOperationException("The store_settings row is missing. Apply the database migrations.");

        return PublicStoreResponse.From(settings);
    }
}
