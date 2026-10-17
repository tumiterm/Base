using Base.Application.Operations;

namespace Base.Application.Releases;

/// <summary>How a gated feature reaches tenants that haven't adopted its release yet.</summary>
public enum ReleaseRollout
{
    /// <summary>Off until the tenant adopts the release or switches it on. For new screens and workflows.</summary>
    OptIn = 0,

    /// <summary>On straight away; a tenant can keep the old behaviour until it adopts the release or the feature retires.</summary>
    OnByDefault = 1,
}

/// <summary>
/// A user-visible change that tenants take when they are ready, rather than on deployment. Fixes and polish are not
/// release features: they reach everyone.
/// </summary>
/// <param name="Code">Constant in <see cref="ReleaseFeatureCodes"/>, e.g. <c>Release.PeopleTimeline</c>.</param>
/// <param name="Introduced">The version (SemVer) that ships it.</param>
/// <param name="PlanFeature">A plan feature (<c>FeatureCodes</c>) the tenant's plan must also include, if any.</param>
/// <param name="Retired">
/// The version from which it is on for everyone and the old code path can be deleted. Set it once every tenant has
/// it, or when the support window has moved everyone past it; then remove the flag and, later, the entry.
/// </param>
public sealed record ReleaseFeature(
    string Code,
    string Name,
    string Description,
    string Introduced,
    ReleaseRollout Rollout = ReleaseRollout.OptIn,
    string? PlanFeature = null,
    string? Retired = null);

/// <summary>Codes of gated features. Adding one = a constant here + a <see cref="ReleaseCatalog"/> entry + a gate.</summary>
public static class ReleaseFeatureCodes
{
}

/// <summary>
/// The features tenants adopt release by release. Empty in the Base itself: its changes reach everyone. A project
/// adds an entry when it ships a change its customers should take at their own pace, and gates the code with
/// <c>ITenantReleases.IsOnAsync</c>, <c>[RequiresRelease]</c> or <c>asp-release</c>.
/// </summary>
public static class ReleaseCatalog
{
    public static IReadOnlyList<ReleaseFeature> All { get; } = [];
}

/// <summary>The catalogue and the running version, as the release services see them (replaceable in tests).</summary>
public interface IReleaseCatalog
{
    /// <summary>The running product version (SemVer).</summary>
    string CurrentVersion { get; }

    IReadOnlyList<ReleaseFeature> Features { get; }

    ReleaseFeature? Find(string code) => Features.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.Ordinal));
}

internal sealed class DefaultReleaseCatalog : IReleaseCatalog
{
    public string CurrentVersion => AppVersion.Version;

    public IReadOnlyList<ReleaseFeature> Features => ReleaseCatalog.All;
}
