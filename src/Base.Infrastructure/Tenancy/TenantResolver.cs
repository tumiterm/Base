using System.ComponentModel;
using Base.Domain.Tenancy;
using Base.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace Base.Infrastructure.Tenancy;

[ImmutableObject(true)]
public sealed record ResolvedTenant(Guid Id, string Code, bool IsActive);

public interface ITenantResolver
{
    Task<ResolvedTenant?> ResolveByHostAsync(string host, CancellationToken cancellationToken = default);

    Task<ResolvedTenant?> GetDefaultAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets cached lookups after a tenant or domain changes (deactivated, domain added or removed).</summary>
    Task InvalidateAsync(CancellationToken cancellationToken = default);
}

/// <summary>Looks tenants up by verified host name or code. Results are cached briefly.</summary>
internal sealed class TenantResolver(AppDbContext db, HybridCache cache, IOptions<TenancyOptions> options) : ITenantResolver
{
    private const string CacheTag = "tenants";
    private static readonly HybridCacheEntryOptions CacheOptions = new() { Expiration = TimeSpan.FromMinutes(5) };
    private static readonly string[] CacheTags = [CacheTag];

    public Task<ResolvedTenant?> ResolveByHostAsync(string host, CancellationToken cancellationToken = default)
    {
        var normalized = TenantDomain.Normalize(host);
        return cache.GetOrCreateAsync(
            $"tenant:host:{normalized}",
            (db, normalized),
            static async (state, ct) => await state.db.TenantDomains.AsNoTracking()
                .Where(d => d.Host == state.normalized && d.IsVerified)
                .Join(state.db.Tenants, d => d.TenantId, t => t.Id, (_, t) => new ResolvedTenant(t.Id, t.Code, t.IsActive))
                .FirstOrDefaultAsync(ct),
            CacheOptions,
            CacheTags,
            cancellationToken).AsTask();
    }

    public Task<ResolvedTenant?> GetDefaultAsync(CancellationToken cancellationToken = default)
    {
        var code = options.Value.DefaultTenantCode;
        return cache.GetOrCreateAsync(
            $"tenant:code:{code}",
            (db, code),
            static async (state, ct) => await state.db.Tenants.AsNoTracking()
                .Where(t => t.Code == state.code)
                .Select(t => new ResolvedTenant(t.Id, t.Code, t.IsActive))
                .FirstOrDefaultAsync(ct),
            CacheOptions,
            CacheTags,
            cancellationToken).AsTask();
    }

    public Task InvalidateAsync(CancellationToken cancellationToken = default) => cache.RemoveByTagAsync(CacheTag, cancellationToken).AsTask();
}
