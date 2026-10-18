using Base.Application.Releases;
using Base.Application.Subscriptions;

namespace Base.UnitTests.Security;

public sealed class EntitlementCatalogTests
{
    [Fact]
    public void Every_feature_and_limit_belongs_to_a_module_and_every_module_has_some() =>
        EntitlementCatalog.Modules.ShouldAllBe(m => EntitlementCatalog.In(m.Code).Any());

    [Fact]
    public void Entries_name_a_known_module_a_version_and_unique_codes()
    {
        var modules = EntitlementCatalog.Modules.Select(m => m.Code).ToHashSet(StringComparer.Ordinal);
        EntitlementCatalog.All.ShouldAllBe(e => modules.Contains(e.Module));
        EntitlementCatalog.All.Select(e => e.Code).ShouldBeUnique();
        EntitlementCatalog.Modules.Select(m => m.Code).ShouldBeUnique();
        foreach (var entry in EntitlementCatalog.All)
        {
            ReleaseVersions.Parse(entry.Since).ShouldBeGreaterThan(new Version(0, 0, 0), entry.Code);
        }
    }

    [Fact]
    public void A_features_suggested_tier_sets_what_an_undecided_plan_gets()
    {
        EntitlementCatalog.Features.ShouldAllBe(f => f.Tier != null);
        EntitlementCatalog.Limits.ShouldAllBe(l => l.Tier == null);
        EntitlementCatalog.Get(FeatureCodes.Documents).ShouldSatisfyAllConditions(f => f.Tier.ShouldBe(FeatureTier.Standard), f => f.DefaultValue.ShouldBe(1));
        EntitlementCatalog.Get(FeatureCodes.DocumentGeneration).ShouldSatisfyAllConditions(f => f.Tier.ShouldBe(FeatureTier.Premium), f => f.DefaultValue.ShouldBe(0));
    }
}
