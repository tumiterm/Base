using Base.Application.Releases;

namespace Base.UnitTests.Settings;

public sealed class ReleaseRulesTests
{
    private static readonly ReleaseFeature OptIn = new("Release.A", "A", "A", "1.1.0");
    private static readonly ReleaseFeature ByDefault = new("Release.B", "B", "B", "1.2.0", ReleaseRollout.OnByDefault);
    private static readonly ReleaseFeature Retired = new("Release.C", "C", "C", "1.1.0", Retired: "1.3.0");
    private static readonly ReleaseFeature[] All = [OptIn, ByDefault, Retired];

    [Theory]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("1.2.0-beta.1+abc", "1.2.0", 0)]
    [InlineData("0.9.9", "1.0.0", -1)]
    public void Versions_compare_by_number_not_text(string left, string right, int expected) =>
        Math.Sign(ReleaseVersions.Compare(left, right)).ShouldBe(expected);

    [Fact]
    public void Versions_read_as_people_say_them() =>
        (ReleaseVersions.Display("1.4.0"), ReleaseVersions.Display("1.4.2"), ReleaseVersions.Label("0.0.0")).ShouldBe(("1.4", "1.4.2", "None yet"));

    [Fact]
    public void A_feature_follows_choice_then_release_then_rollout()
    {
        ReleaseRules.StateOf(OptIn, "1.0.0", "1.3.0", null, inPlan: true).ShouldSatisfyAllConditions(s => s.IsOn.ShouldBeFalse(), s => s.Source.ShouldBe(FeatureStateSource.Default));
        ReleaseRules.StateOf(OptIn, "1.1.0", "1.3.0", null, inPlan: true).Source.ShouldBe(FeatureStateSource.Adopted);
        ReleaseRules.StateOf(OptIn, "1.0.0", "1.3.0", true, inPlan: true).ShouldSatisfyAllConditions(s => s.IsOn.ShouldBeTrue(), s => s.Source.ShouldBe(FeatureStateSource.Choice));
        ReleaseRules.StateOf(ByDefault, "1.0.0", "1.3.0", null, inPlan: true).IsOn.ShouldBeTrue();
        ReleaseRules.StateOf(ByDefault, "1.0.0", "1.3.0", null, inPlan: false).ShouldSatisfyAllConditions(s => s.IsOn.ShouldBeFalse(), s => s.CanChoose.ShouldBeFalse());
        ReleaseRules.StateOf(Retired, "1.0.0", "1.3.0", false, inPlan: true).ShouldSatisfyAllConditions(s => s.IsOn.ShouldBeTrue(), s => s.CanChoose.ShouldBeFalse());
        ReleaseRules.StateOf(ByDefault, "1.0.0", "1.1.0", true, inPlan: true).IsOn.ShouldBeFalse("not in this build yet");
    }

    [Fact]
    public void Pending_releases_leave_out_retired_and_unreleased_features()
    {
        ReleaseRules.PendingVersions(All, "1.0.0", "1.3.0").ShouldBe(["1.1", "1.2"]);
        ReleaseRules.PendingVersions(All, "1.0.0", "1.1.0").ShouldBe(["1.1"]);
        ReleaseRules.PendingVersions(All, "1.2.0", "1.3.0").ShouldBeEmpty();
        ReleaseRules.EffectiveVersion(All, "1.2.0", "1.3.0").ShouldBe("1.3");
        ReleaseRules.EffectiveVersion(All, "1.0.0", "1.3.0").ShouldBe("1.0");
    }
}

public sealed class ReleaseCatalogTests
{
    [Fact]
    public void Codes_and_the_catalogue_match_and_every_entry_is_well_formed()
    {
        var codes = typeof(ReleaseFeatureCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        codes.ShouldBe(ReleaseCatalog.All.Select(f => f.Code), ignoreOrder: true);
        ReleaseCatalog.All.Select(f => f.Code).ShouldBeUnique();
        ReleaseCatalog.All.ShouldAllBe(f => f.Code.StartsWith("Release.", StringComparison.Ordinal)
            && ReleaseVersions.Compare(f.Introduced, "0.0.0") > 0
            && (f.Retired == null || ReleaseVersions.Compare(f.Retired, f.Introduced) > 0)
            && (f.PlanFeature == null || Base.Application.Subscriptions.EntitlementCatalog.Find(f.PlanFeature) != null));
    }
}
