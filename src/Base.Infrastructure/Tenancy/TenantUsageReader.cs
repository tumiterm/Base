using Base.Application.Common.Abstractions;
using Base.Application.Subscriptions;
using Base.Domain.Notifications;
using Base.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Base.Infrastructure.Tenancy;

/// <summary>
/// Counts what plan limits measure: user accounts that aren't locked, bytes of stored (not deleted) files, and messages
/// sent from Messages this calendar month (UTC), not counting those skipped.
/// </summary>
internal sealed class TenantUsageReader(AppDbContext db, ITenantContext tenant, TimeProvider time) : ITenantUsageReader
{
    public async Task<IReadOnlyDictionary<string, long>> MeasureAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var system = tenant.BeginSystemScope();
        var now = time.GetUtcNow();
        var users = await db.Users.CountAsync(u => u.TenantId == tenantId && (u.LockoutEnd == null || u.LockoutEnd <= now), cancellationToken);
        var bytes = await db.StoredFiles.Where(f => f.TenantId == tenantId).SumAsync(f => (long?)f.SizeBytes, cancellationToken) ?? 0;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var messages = await db.NotificationMessages.CountAsync(
            m => m.TenantId == tenantId && m.SourceId != null && m.DateCreated >= monthStart && m.Status != NotificationStatus.Skipped, cancellationToken);
        return new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [LimitCodes.Users] = users,
            [LimitCodes.StorageMb] = bytes,
            [LimitCodes.MessagesPerMonth] = messages,
        };
    }
}
