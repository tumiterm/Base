using System.ComponentModel;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Settings;
using Base.Application.Subscriptions;
using Base.Domain.Integrations;
using Microsoft.Extensions.Caching.Hybrid;

namespace Base.Application.Integrations;

public enum ApiKeyFailure
{
    /// <summary>Malformed, unknown, revoked or expired: indistinguishable to the caller (401).</summary>
    Invalid = 0,

    /// <summary>A valid key, but the tenant's plan doesn't include API access (403).</summary>
    NotInPlan = 1,
}

public sealed record ApiKeyResult(ApiClientIdentity? Client, ApiKeyFailure Failure);

/// <summary>Checks an API key for the current tenant. Keys only work in the tenant they belong to.</summary>
public interface IApiKeyAuthenticator
{
    Task<ApiKeyResult> AuthenticateAsync(string key, CancellationToken cancellationToken = default);
}

internal sealed class ApiKeyAuthenticator(
    IUnitOfWork unitOfWork,
    ITenantContext tenant,
    ITenantEntitlements entitlements,
    IApplicationSettingService settings,
    HybridCache cache,
    TimeProvider time) : IApiKeyAuthenticator
{
    private static readonly ApiKeyResult Invalid = new(null, ApiKeyFailure.Invalid);
    private static readonly HybridCacheEntryOptions CacheOptions = new() { Expiration = TimeSpan.FromMinutes(1) };
    private static readonly string[] CacheTags = [ApiClientAdministration.CacheTag];
    private static readonly TimeSpan LastUsedPrecision = TimeSpan.FromMinutes(5);

    public async Task<ApiKeyResult> AuthenticateAsync(string key, CancellationToken cancellationToken = default)
    {
        if (ApiKeys.PrefixOf(key) is not { } prefix || !tenant.IsResolved)
        {
            return Invalid;
        }

        // Cached briefly (and dropped on any change to a client), so a busy integration doesn't cost a query per call.
        var client = await cache.GetOrCreateAsync(
            $"api-client:{tenant.TenantId}:{prefix}",
            (unitOfWork, prefix),
            static async (state, ct) => await state.unitOfWork.Repository<ApiClient>().GetAsync(
                c => c.KeyPrefix == state.prefix,
                c => new ApiClientSnapshot(c.Id, c.Name, c.TenantId, c.KeyHash, c.Permissions, c.RequestsPerMinute, c.ExpiresAt, c.RevokedAt != null, c.LastUsedAt),
                ct),
            CacheOptions,
            CacheTags,
            cancellationToken);

        var now = time.GetUtcNow();
        if (client is null || client.Revoked || client.ExpiresAt <= now || !ApiKeys.Matches(key, client.KeyHash))
        {
            return Invalid;
        }

        if (!await entitlements.HasFeatureAsync(FeatureCodes.Api, cancellationToken))
        {
            return new ApiKeyResult(null, ApiKeyFailure.NotInPlan);
        }

        if (client.LastUsedAt is null || client.LastUsedAt < now - LastUsedPrecision)
        {
            await MarkUsedAsync(client.Id, now, cancellationToken);
        }

        var limit = client.RequestsPerMinute ?? await settings.GetIntAsync(SettingCodes.Api.DefaultRequestsPerMinute, cancellationToken);
        return new ApiKeyResult(new ApiClientIdentity(client.Id, client.Name, client.TenantId, client.Permissions, limit), ApiKeyFailure.Invalid);
    }

    private async Task MarkUsedAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tracked = await unitOfWork.Repository<ApiClient>().GetByIdAsync(id, cancellationToken);
        if (tracked is not null)
        {
            tracked.MarkUsed(now);
            await unitOfWork.SaveAsync(cancellationToken);
            await cache.RemoveAsync($"api-client:{tenant.TenantId}:{tracked.KeyPrefix}", cancellationToken);
        }
    }

    [ImmutableObject(true)]
    public sealed record ApiClientSnapshot(
        Guid Id, string Name, Guid TenantId, string KeyHash, IReadOnlyList<string> Permissions, int? RequestsPerMinute, DateTimeOffset ExpiresAt, bool Revoked, DateTimeOffset? LastUsedAt);
}
