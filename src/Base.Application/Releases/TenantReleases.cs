using System.ComponentModel;
using System.Globalization;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Operations;
using Base.Application.Settings;
using Base.Application.Subscriptions;
using Base.Domain.Tenancy;
using Microsoft.Extensions.Caching.Hybrid;

namespace Base.Application.Releases;

public sealed record PendingRelease(string Version, IReadOnlyList<FeatureState> Features);

/// <summary>Where one tenant stands: its adopted release, what is waiting for it, and every feature's state.</summary>
/// <param name="MaxBehind">The support window: at most this many releases behind (null: no limit).</param>
public sealed record TenantReleaseState(
    string CurrentVersion,
    string AdoptedVersion,
    string EffectiveVersion,
    DateTimeOffset? AdoptedAt,
    IReadOnlyList<PendingRelease> Pending,
    IReadOnlyList<FeatureState> Taken,
    int? MaxBehind)
{
    public int Behind => Pending.Count;

    public bool IsUpToDate => Pending.Count == 0;

    /// <summary>Pending features already on (tried early, or on by default).</summary>
    public int OnEarly => Pending.Sum(r => r.Features.Count(f => f.IsOn));
}

public sealed record VersionShare(string Version, int Tenants);

public sealed record FeatureAdoption(ReleaseFeature Feature, int OnCount);

/// <summary>Every tenant's release position, for the platform.</summary>
public sealed record ReleaseFleet(string CurrentVersion, int TenantCount, int UpToDate, IReadOnlyList<VersionShare> Versions, IReadOnlyList<FeatureAdoption> Features);

/// <summary>
/// Release adoption per tenant (ADR 0022): which gated features a tenant has, taking new releases, trying features
/// early or keeping the old way. Gate code with <see cref="IsOnAsync"/>, or <c>[RequiresRelease]</c> / <c>asp-release</c>.
/// </summary>
public interface ITenantReleases
{
    /// <summary>Whether a catalogued feature is on for the current tenant (cached).</summary>
    Task<bool> IsOnAsync(string featureCode, CancellationToken cancellationToken = default);

    Task<TenantReleaseState> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Another tenant's position (platform screens); plan checks are left out.</summary>
    Task<TenantReleaseState?> GetForAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Switches one feature on or off for the current tenant, whatever its release says.</summary>
    Task<ValidationResponse> ChooseAsync(string featureCode, bool isOn, CancellationToken cancellationToken = default);

    /// <summary>Drops the tenant's choice so the feature follows its release again.</summary>
    Task<ValidationResponse> ClearChoiceAsync(string featureCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes every release up to <paramref name="version"/> (default: the running one). Its features switch on and
    /// earlier choices about them are dropped.
    /// </summary>
    Task<ValidationResponse> AdoptAsync(string? version = null, CancellationToken cancellationToken = default);

    Task<ReleaseFleet> FleetAsync(CancellationToken cancellationToken = default);
}

internal sealed class TenantReleases(
    IUnitOfWork unitOfWork,
    ITenantContext tenant,
    IReleaseCatalog catalog,
    ITenantEntitlements entitlements,
    IApplicationSettingService settings,
    HybridCache cache,
    TimeProvider time) : ITenantReleases
{
    internal const string CacheTag = "releases";
    private static readonly HybridCacheEntryOptions CacheOptions = new() { Expiration = TimeSpan.FromMinutes(5) };
    private static readonly string[] CacheTags = [CacheTag];

    public async Task<bool> IsOnAsync(string featureCode, CancellationToken cancellationToken = default)
    {
        var feature = catalog.Find(featureCode) ?? throw new KeyNotFoundException($"Release feature '{featureCode}' is not in the ReleaseCatalog.");
        var snapshot = await SnapshotAsync(cancellationToken);
        return (await StateAsync(feature, snapshot, checkPlan: true, cancellationToken)).IsOn;
    }

    public async Task<TenantReleaseState> GetAsync(CancellationToken cancellationToken = default) =>
        await BuildAsync(await SnapshotAsync(cancellationToken), checkPlan: true, cancellationToken);

    public async Task<TenantReleaseState?> GetForAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var system = tenant.BeginSystemScope();
        var snapshot = await LoadAsync(unitOfWork, tenantId, cancellationToken);
        return snapshot is null ? null : await BuildAsync(snapshot, checkPlan: false, cancellationToken);
    }

    public async Task<ValidationResponse> ChooseAsync(string featureCode, bool isOn, CancellationToken cancellationToken = default)
    {
        var feature = catalog.Find(featureCode);
        if (feature is null)
        {
            return ValidationResponse.NotFound("That feature doesn't exist.");
        }

        var state = await StateAsync(feature, await SnapshotAsync(cancellationToken), checkPlan: true, cancellationToken);
        if (!state.CanChoose)
        {
            return ValidationResponse.Conflict(state.Source == FeatureStateSource.NotInPlan
                ? $"{feature.Name} isn't included in your plan."
                : $"{feature.Name} is part of every organisation now and can't be switched off.");
        }

        var repository = unitOfWork.Repository<TenantFeatureChoice>();
        var choice = await repository.GetAsync(c => c.FeatureCode == feature.Code, cancellationToken: cancellationToken);
        if (choice is null)
        {
            await repository.AddAsync(new TenantFeatureChoice(feature.Code, isOn, time.GetUtcNow()), cancellationToken);
        }
        else
        {
            choice.Change(isOn, time.GetUtcNow());
        }

        await unitOfWork.SaveAsync(cancellationToken);
        await cache.RemoveAsync(CacheKey(tenant.TenantId), cancellationToken);
        return ValidationResponse.Success(isOn ? $"{feature.Name} is on." : $"{feature.Name} is off.");
    }

    public async Task<ValidationResponse> ClearChoiceAsync(string featureCode, CancellationToken cancellationToken = default)
    {
        var feature = catalog.Find(featureCode);
        if (feature is null)
        {
            return ValidationResponse.NotFound("That feature doesn't exist.");
        }

        var repository = unitOfWork.Repository<TenantFeatureChoice>();
        if (await repository.GetAsync(c => c.FeatureCode == feature.Code, cancellationToken: cancellationToken) is { } choice)
        {
            repository.Remove(choice);
            await unitOfWork.SaveAsync(cancellationToken);
            await cache.RemoveAsync(CacheKey(tenant.TenantId), cancellationToken);
        }

        return ValidationResponse.Success($"{feature.Name} follows your release again.");
    }

    public async Task<ValidationResponse> AdoptAsync(string? version = null, CancellationToken cancellationToken = default)
    {
        var target = ReleaseVersions.Display(version ?? catalog.CurrentVersion);
        if (ReleaseVersions.Compare(target, catalog.CurrentVersion) > 0)
        {
            return ValidationResponse.Failure($"Version {target} isn't released yet.");
        }

        var adopted = await AdoptCoreAsync(unitOfWork, catalog, tenant.TenantId, target, time.GetUtcNow(), cancellationToken);
        if (adopted is null)
        {
            return ValidationResponse.NotFound("Organisation not found.");
        }

        await cache.RemoveAsync(CacheKey(tenant.TenantId), cancellationToken);
        return ValidationResponse.Success(adopted == 0
            ? $"You're on {target}."
            : string.Create(CultureInfo.InvariantCulture, $"You're on {target}: {adopted} new feature{(adopted == 1 ? " is" : "s are")} on."));
    }

    public async Task<ReleaseFleet> FleetAsync(CancellationToken cancellationToken = default)
    {
        using var system = tenant.BeginSystemScope();
        var tenants = await unitOfWork.Repository<Tenant>().GetAllAsync(t => new { t.Id, t.ReleaseVersion }, t => t.IsActive, cancellationToken: cancellationToken);
        var choices = await unitOfWork.Repository<TenantFeatureChoice>().GetAllAsync(c => new { c.TenantId, c.FeatureCode, c.IsOn }, cancellationToken: cancellationToken);
        var byTenant = choices.GroupBy(c => c.TenantId).ToDictionary(g => g.Key, g => g.ToDictionary(c => c.FeatureCode, c => c.IsOn, StringComparer.Ordinal));
        var current = catalog.CurrentVersion;

        var versions = tenants
            .GroupBy(t => ReleaseRules.EffectiveVersion(catalog.Features, t.ReleaseVersion, current), StringComparer.Ordinal)
            .Select(g => new VersionShare(g.Key, g.Count()))
            .OrderByDescending(v => ReleaseVersions.Parse(v.Version))
            .ToList();

        var features = catalog.Features
            .Select(f => new FeatureAdoption(f, tenants.Count(t =>
                ReleaseRules.StateOf(f, t.ReleaseVersion, current, byTenant.GetValueOrDefault(t.Id)?.GetValueOrDefault(f.Code) is { } on ? on : null, inPlan: true).IsOn)))
            .ToList();

        var upToDate = tenants.Count(t => ReleaseRules.PendingVersions(catalog.Features, t.ReleaseVersion, current).Count == 0);
        return new ReleaseFleet(ReleaseVersions.Display(current), tenants.Count, upToDate, versions, features);
    }

    /// <summary>Moves a tenant to <paramref name="version"/>; returns how many features that switched on, or null if it doesn't exist.</summary>
    internal static async Task<int?> AdoptCoreAsync(IUnitOfWork unitOfWork, IReleaseCatalog catalog, Guid tenantId, string version, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var target = await unitOfWork.Repository<Tenant>().GetAsync(t => t.Id == tenantId, cancellationToken: cancellationToken);
        if (target is null)
        {
            return null;
        }

        var previous = target.ReleaseVersion;
        var arriving = catalog.Features
            .Where(f => ReleaseVersions.Compare(f.Introduced, previous) > 0 && ReleaseVersions.Compare(f.Introduced, version) <= 0)
            .Select(f => f.Code)
            .ToHashSet(StringComparer.Ordinal);
        if (ReleaseVersions.Compare(version, previous) > 0)
        {
            target.AdoptRelease(version, now);
        }

        var repository = unitOfWork.Repository<TenantFeatureChoice>();
        var choices = await repository.GetAllAsync(c => c.TenantId == tenantId, includeProperties: null, cancellationToken: cancellationToken);
        foreach (var choice in choices.Where(c => arriving.Contains(c.FeatureCode)))
        {
            repository.Remove(choice);
        }

        await unitOfWork.SaveAsync(cancellationToken);
        return arriving.Count;
    }

    private async Task<TenantReleaseState> BuildAsync(ReleaseSnapshot snapshot, bool checkPlan, CancellationToken cancellationToken)
    {
        var current = catalog.CurrentVersion;
        var states = new List<FeatureState>();
        foreach (var feature in catalog.Features.Where(f => ReleaseVersions.Compare(f.Introduced, current) <= 0))
        {
            states.Add(await StateAsync(feature, snapshot, checkPlan, cancellationToken));
        }

        var pendingVersions = ReleaseRules.PendingVersions(catalog.Features, snapshot.Version, current);
        var pending = pendingVersions
            .Select(v => new PendingRelease(v, states.Where(s => string.Equals(ReleaseVersions.Display(s.Feature.Introduced), v, StringComparison.Ordinal) && s.Source != FeatureStateSource.Retired).ToList()))
            .ToList();
        var pendingCodes = pending.SelectMany(r => r.Features).Select(f => f.Feature.Code).ToHashSet(StringComparer.Ordinal);
        var max = await settings.GetIntAsync(SettingCodes.Releases.MaxReleasesBehind, cancellationToken);

        return new TenantReleaseState(
            ReleaseVersions.Display(current),
            ReleaseVersions.Display(snapshot.Version),
            ReleaseRules.EffectiveVersion(catalog.Features, snapshot.Version, current),
            snapshot.AdoptedAt,
            pending,
            states.Where(s => !pendingCodes.Contains(s.Feature.Code)).OrderByDescending(s => ReleaseVersions.Parse(s.Feature.Introduced)).ToList(),
            max > 0 ? max : null);
    }

    private async Task<FeatureState> StateAsync(ReleaseFeature feature, ReleaseSnapshot snapshot, bool checkPlan, CancellationToken cancellationToken)
    {
        var inPlan = !checkPlan || feature.PlanFeature is null || await entitlements.HasFeatureAsync(feature.PlanFeature, cancellationToken);
        return ReleaseRules.StateOf(feature, snapshot.Version, catalog.CurrentVersion, snapshot.Choices.TryGetValue(feature.Code, out var on) ? on : null, inPlan);
    }

    private async Task<ReleaseSnapshot> SnapshotAsync(CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(
            CacheKey(tenant.TenantId),
            (unitOfWork, tenantId: tenant.TenantId),
            static async (state, ct) => await LoadAsync(state.unitOfWork, state.tenantId, ct) ?? ReleaseSnapshot.None,
            CacheOptions,
            CacheTags,
            cancellationToken);

    private static async Task<ReleaseSnapshot?> LoadAsync(IUnitOfWork unitOfWork, Guid tenantId, CancellationToken cancellationToken)
    {
        var row = await unitOfWork.Repository<Tenant>().GetAsync(
            t => t.Id == tenantId, t => new { t.ReleaseVersion, t.ReleaseAdoptedAt }, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var choices = await unitOfWork.Repository<TenantFeatureChoice>().GetAllAsync(
            c => new { c.FeatureCode, c.IsOn }, c => c.TenantId == tenantId, cancellationToken: cancellationToken);
        return new ReleaseSnapshot(row.ReleaseVersion, row.ReleaseAdoptedAt, choices.ToDictionary(c => c.FeatureCode, c => c.IsOn, StringComparer.Ordinal));
    }

    private static string CacheKey(Guid tenantId) => $"releases:{tenantId:N}";

    [ImmutableObject(true)]
    internal sealed record ReleaseSnapshot(string Version, DateTimeOffset? AdoptedAt, Dictionary<string, bool> Choices)
    {
        public static ReleaseSnapshot None { get; } = new(Tenant.NoRelease, null, new Dictionary<string, bool>(StringComparer.Ordinal));
    }
}

/// <summary>
/// The support window: a tenant more than <c>Releases.MaxReleasesBehind</c> releases behind is moved forward to the
/// oldest release that brings it back inside, so old code paths can be retired. Off when the setting is 0.
/// </summary>
internal sealed class ReleaseWindowJob(IUnitOfWork unitOfWork, IReleaseCatalog catalog, IApplicationSettingService settings, HybridCache cache, TimeProvider time) : IScheduledJob
{
    public string Code => "release-window";

    public string Name => "Release support window";

    public string Description => "Moves organisations that fall too many releases behind forward, as Releases.MaxReleasesBehind sets.";

    public TimeSpan Interval => TimeSpan.FromDays(1);

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var max = await settings.GetIntAsync(SettingCodes.Releases.MaxReleasesBehind, cancellationToken);
        if (max <= 0)
        {
            return "No support window set";
        }

        var tenants = await unitOfWork.Repository<Tenant>().GetAllAsync(t => new { t.Id, t.ReleaseVersion }, t => t.IsActive, cancellationToken: cancellationToken);
        var moved = 0;
        foreach (var t in tenants)
        {
            var pending = ReleaseRules.PendingVersions(catalog.Features, t.ReleaseVersion, catalog.CurrentVersion);
            if (pending.Count > max)
            {
                await TenantReleases.AdoptCoreAsync(unitOfWork, catalog, t.Id, pending[pending.Count - max - 1], time.GetUtcNow(), cancellationToken);
                moved++;
            }
        }

        if (moved > 0)
        {
            await cache.RemoveByTagAsync(TenantReleases.CacheTag, cancellationToken);
        }

        return string.Create(CultureInfo.InvariantCulture, $"Moved {moved} organisation(s) forward to stay within {max} release(s)");
    }
}
