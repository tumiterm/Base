namespace Base.Application.Releases;

/// <summary>Where a feature's on/off state comes from, so screens can explain it.</summary>
public enum FeatureStateSource
{
    /// <summary>Its release is adopted.</summary>
    Adopted = 0,

    /// <summary>The tenant chose it (tried it early, or kept the old way).</summary>
    Choice = 1,

    /// <summary>Not adopted yet: off for opt-in features, on for on-by-default ones.</summary>
    Default = 2,

    /// <summary>On for everyone: the old behaviour is gone.</summary>
    Retired = 3,

    /// <summary>Off because the tenant's plan doesn't include it.</summary>
    NotInPlan = 4,
}

public sealed record FeatureState(ReleaseFeature Feature, bool IsOn, FeatureStateSource Source)
{
    /// <summary>Whether the tenant can still switch it (not retired, and in its plan).</summary>
    public bool CanChoose => Source is not (FeatureStateSource.Retired or FeatureStateSource.NotInPlan);
}

/// <summary>The rules, as pure functions over a tenant's adopted version and choices, so every screen and job agrees.</summary>
public static class ReleaseRules
{
    public static FeatureState StateOf(ReleaseFeature feature, string adopted, string current, bool? choice, bool inPlan)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (!inPlan)
        {
            return new(feature, false, FeatureStateSource.NotInPlan);
        }

        if (feature.Retired is { } retired && ReleaseVersions.Compare(retired, current) <= 0)
        {
            return new(feature, true, FeatureStateSource.Retired);
        }

        if (ReleaseVersions.Compare(feature.Introduced, current) > 0)
        {
            return new(feature, false, FeatureStateSource.Default); // catalogued ahead of this build
        }

        if (choice is { } chosen)
        {
            return new(feature, chosen, FeatureStateSource.Choice);
        }

        return ReleaseVersions.Compare(feature.Introduced, adopted) <= 0
            ? new(feature, true, FeatureStateSource.Adopted)
            : new(feature, feature.Rollout == ReleaseRollout.OnByDefault, FeatureStateSource.Default);
    }

    /// <summary>Released versions with features the tenant hasn't adopted, oldest first.</summary>
    public static IReadOnlyList<string> PendingVersions(IEnumerable<ReleaseFeature> features, string adopted, string current) =>
        features
            .Where(f => (f.Retired is null || ReleaseVersions.Compare(f.Retired, current) > 0)
                && ReleaseVersions.Compare(f.Introduced, adopted) > 0
                && ReleaseVersions.Compare(f.Introduced, current) <= 0)
            .Select(f => ReleaseVersions.Display(f.Introduced))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(ReleaseVersions.Parse)
            .ToList();

    /// <summary>What the tenant is effectively on: the running version when nothing is pending, else its adopted one.</summary>
    public static string EffectiveVersion(IEnumerable<ReleaseFeature> features, string adopted, string current) =>
        PendingVersions(features, adopted, current).Count == 0 ? ReleaseVersions.Display(current) : ReleaseVersions.Display(adopted);
}
