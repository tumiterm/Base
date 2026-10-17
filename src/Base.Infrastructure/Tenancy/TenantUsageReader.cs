using Base.Application.Common.Abstractions;
using Base.Application.Subscriptions;
using Base.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Base.Infrastructure.Tenancy;

/// <summary>Counts what plan limits measure: user accounts, and bytes of stored (not deleted) files.</summary>
internal sealed class TenantUsageReader(AppDbContext db, ITenantContext tenant) : ITenantUsageReader
{
    public async Task<IReadOnlyDictionary<string, long>> MeasureAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var system = tenant.BeginSystemScope();
        var users = await db.Users.CountAsync(u => u.TenantId == tenantId, cancellationToken);
        var bytes = await db.StoredFiles.Where(f => f.TenantId == tenantId).SumAsync(f => (long?)f.SizeBytes, cancellationToken) ?? 0;
        return new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [LimitCodes.Users] = users,
            [LimitCodes.StorageMb] = bytes,
        };
    }
}
