using System.ComponentModel;
using System.Globalization;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Domain.Subscriptions;
using Microsoft.Extensions.Caching.Hybrid;

namespace Base.Application.Subscriptions;

/// <summary>
/// What the current tenant's subscription allows. A tenant without a subscription (the platform tenant, or a
/// single-tenant deployment) is unrestricted: every feature on, no limits.
/// </summary>
public interface ITenantEntitlements
{
    Task<TenantAccess> GetAsync(CancellationToken cancellationToken = default);

    Task<bool> HasFeatureAsync(string featureCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fails, naming the limit, when adding <paramref name="adding"/> (in measured units: accounts, bytes) would go over
    /// the plan's limit.
    /// </summary>
    Task<ValidationResponse> EnsureWithinLimitAsync(string limitCode, long adding = 1, CancellationToken cancellationToken = default);

    /// <summary>Drops cached entitlements of every tenant, after a plan or subscription change.</summary>
    Task InvalidateAsync(CancellationToken cancellationToken = default);
}

/// <summary>The current tenant's plan and status as the rest of the application sees it.</summary>
/// <param name="Scheduled">The plan it moves to when the period ends, if a change is waiting: until then the stricter of
/// the two plans' limits on what is kept (users, storage) applies, so the change can still happen.</param>
[ImmutableObject(true)]
public sealed record TenantAccess(
    bool IsRestricted,
    string? PlanCode,
    string? PlanName,
    SubscriptionStatus? Status,
    DateTimeOffset? PeriodEnd,
    bool CancelAtPeriodEnd,
    IReadOnlyDictionary<string, long?> Values,
    ScheduledAccess? Scheduled = null)
{
    public static TenantAccess Unrestricted { get; } = new(false, null, null, null, null, false, new Dictionary<string, long?>(StringComparer.Ordinal));

    /// <summary>False when suspended, cancelled or expired: only the subscription page and sign-out stay open.</summary>
    public bool HasAccess => Status is null or SubscriptionStatus.Trialing or SubscriptionStatus.Active or SubscriptionStatus.PastDue;

    public bool HasFeature(string featureCode) => !IsRestricted || Value(featureCode) == 1;

    /// <summary>The limit in its own units (MB for storage); null means unlimited.</summary>
    public long? LimitOf(string limitCode) => IsRestricted ? Stricter(Value(limitCode), ScheduledLimitOf(limitCode)) : null;

    /// <summary>Whether the plan it moves to sets the limit that applies now.</summary>
    public bool IsLimitedByScheduledPlan(string limitCode) =>
        IsRestricted && ScheduledLimitOf(limitCode) is { } next && (Value(limitCode) is not { } current || next < current);

    private long? ScheduledLimitOf(string code) =>
        Scheduled is null || EntitlementCatalog.Get(code).ResetsMonthly
            ? null
            : Scheduled.Values.TryGetValue(code, out var value) ? value : EntitlementCatalog.Get(code).DefaultValue;

    private static long? Stricter(long? a, long? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    private long? Value(string code) =>
        Values.TryGetValue(code, out var value) ? value : EntitlementCatalog.Get(code).DefaultValue;
}

/// <summary>The plan a tenant moves to at <paramref name="At"/>, and what it allows.</summary>
public sealed record ScheduledAccess(string PlanName, DateTimeOffset At, IReadOnlyDictionary<string, long?> Values);

internal sealed class TenantEntitlements(IUnitOfWork unitOfWork, ITenantContext tenant, ITenantUsageReader usage, HybridCache cache) : ITenantEntitlements
{
    private const string CacheTag = "entitlements";
    private static readonly HybridCacheEntryOptions CacheOptions = new() { Expiration = TimeSpan.FromMinutes(5) };
    private static readonly string[] CacheTags = [CacheTag];

    public async Task<TenantAccess> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!tenant.IsResolved || tenant.IsSystemScope)
        {
            return TenantAccess.Unrestricted;
        }

        return await cache.GetOrCreateAsync(
            $"entitlements:{tenant.TenantId}",
            this,
            static async (self, ct) => await self.LoadAsync(ct),
            CacheOptions,
            CacheTags,
            cancellationToken);
    }

    public async Task<bool> HasFeatureAsync(string featureCode, CancellationToken cancellationToken = default) =>
        (await GetAsync(cancellationToken)).HasFeature(featureCode);

    public async Task<ValidationResponse> EnsureWithinLimitAsync(string limitCode, long adding = 1, CancellationToken cancellationToken = default)
    {
        var definition = EntitlementCatalog.Get(limitCode);
        var access = await GetAsync(cancellationToken);
        if (access.LimitOf(limitCode) is not { } limit)
        {
            return ValidationResponse.Success();
        }

        var used = (await usage.MeasureAsync(tenant.TenantId, cancellationToken)).GetValueOrDefault(limitCode);
        if (used + adding <= limit * definition.UnitSize)
        {
            return ValidationResponse.Success();
        }

        return ValidationResponse.Failure(
            access.IsLimitedByScheduledPlan(limitCode)
                ? string.Create(CultureInfo.InvariantCulture, $"Your plan changes to {access.Scheduled!.PlanName} on {access.Scheduled.At:d MMMM yyyy}, which allows {definition.Format(limit)} of {definition.Name.ToLowerInvariant()}. Keep your current plan to add more.")
                : $"Your {access.PlanName} plan allows {definition.Format(limit)} of {definition.Name.ToLowerInvariant()}. Upgrade the plan to add more.",
            "plan.limit");
    }

    public async Task InvalidateAsync(CancellationToken cancellationToken = default) =>
        await cache.RemoveByTagAsync(CacheTag, cancellationToken);

    private async Task<TenantAccess> LoadAsync(CancellationToken cancellationToken)
    {
        var subscription = await unitOfWork.Repository<TenantSubscription>().GetAsync(s => true, asNoTracking: true, cancellationToken: cancellationToken);
        if (subscription is null)
        {
            return TenantAccess.Unrestricted;
        }

        var plan = await unitOfWork.Repository<Plan>().GetAsync(p => p.Id == subscription.PlanId, [p => p.Entitlements], asNoTracking: true, cancellationToken);
        var values = plan?.Entitlements.ToDictionary(e => e.Code, e => e.Value, StringComparer.Ordinal) ?? new Dictionary<string, long?>(StringComparer.Ordinal);
        ScheduledAccess? scheduled = null;
        if (subscription.ScheduledPlanId is { } nextId
            && await unitOfWork.Repository<Plan>().GetAsync(p => p.Id == nextId, [p => p.Entitlements], asNoTracking: true, cancellationToken) is { } next)
        {
            scheduled = new ScheduledAccess(next.Name, subscription.ScheduledChangeAt!.Value, next.Entitlements.ToDictionary(e => e.Code, e => e.Value, StringComparer.Ordinal));
        }

        return new TenantAccess(
            true,
            plan?.Code,
            plan?.Name,
            subscription.Status,
            subscription.CurrentPeriodEnd,
            subscription.CancelAtPeriodEnd,
            values,
            scheduled);
    }
}
