using System.Reflection;
using Base.Application.Flags;
using Base.Domain.Flags;

namespace Base.UnitTests.Flags;

public sealed class FeatureFlagRulesTests
{
    private const string Code = "Flag.Test";

    private static readonly IReadOnlyList<Guid> Tenants = Enumerable.Range(1, 1000).Select(i => new Guid(i, 0, 0, new byte[8])).ToList();

    [Fact]
    public void Off_and_everyone_mean_what_they_say_and_an_override_wins()
    {
        var tenant = Guid.NewGuid();
        FeatureFlagRules.IsOn(Code, Setting(FeatureFlagMode.Off), tenant, isPlatform: true).ShouldBeFalse();
        FeatureFlagRules.IsOn(Code, Setting(FeatureFlagMode.Everyone), tenant, isPlatform: false).ShouldBeTrue();
        FeatureFlagRules.IsOn(Code, Setting(FeatureFlagMode.Everyone), null, isPlatform: false).ShouldBeTrue("on for everyone includes work outside an organisation");
        FeatureFlagRules.IsOn(Code, Setting(FeatureFlagMode.Off), tenant, false, overridden: true).ShouldBeTrue();
        FeatureFlagRules.IsOn(Code, Setting(FeatureFlagMode.Everyone), tenant, false, overridden: false).ShouldBeFalse();
    }

    [Fact]
    public void Targeted_is_on_for_the_platform_the_chosen_and_nobody_outside_an_organisation()
    {
        var chosen = Guid.NewGuid();
        var other = Tenants.First(t => FeatureFlagRules.Bucket(Code, t) >= 10);
        var setting = Setting(FeatureFlagMode.Targeted, 10, [chosen], includePlatform: true);

        FeatureFlagRules.IsOn(Code, setting, chosen, false).ShouldBeTrue();
        FeatureFlagRules.IsOn(Code, setting, other, isPlatform: true).ShouldBeTrue();
        FeatureFlagRules.IsOn(Code, setting, other, isPlatform: false).ShouldBeFalse();
        FeatureFlagRules.IsOn(Code, setting, null, false).ShouldBeFalse();
    }

    [Fact]
    public void A_percentage_reaches_about_that_share_and_growing_it_keeps_those_already_in()
    {
        var at10 = Tenants.Where(t => FeatureFlagRules.IsOn(Code, Setting(FeatureFlagMode.Targeted, 10), t, false)).ToHashSet();
        var at50 = Tenants.Where(t => FeatureFlagRules.IsOn(Code, Setting(FeatureFlagMode.Targeted, 50), t, false)).ToHashSet();

        at10.Count.ShouldBeInRange(60, 140);
        at50.Count.ShouldBeInRange(430, 570);
        at10.IsSubsetOf(at50).ShouldBeTrue();
        Tenants.Count(t => FeatureFlagRules.IsOn(Code, Setting(FeatureFlagMode.Targeted, 100), t, false)).ShouldBe(Tenants.Count);
        Tenants.Count(t => FeatureFlagRules.IsOn(Code, Setting(FeatureFlagMode.Targeted, 0), t, false)).ShouldBe(0);
    }

    [Fact]
    public void Buckets_are_stable_and_differ_between_flags()
    {
        var tenant = Tenants[0];
        FeatureFlagRules.Bucket(Code, tenant).ShouldBe(FeatureFlagRules.Bucket(Code, tenant));
        Tenants.Count(t => FeatureFlagRules.Bucket(Code, t) == FeatureFlagRules.Bucket("Flag.Other", t)).ShouldBeLessThan(50, "the same organisations shouldn't always go first");
    }

    [Fact]
    public void Saving_anything_but_targeted_forgets_the_targeting()
    {
        var state = new FeatureFlagState(Code);
        state.Set(FeatureFlagMode.Targeted, 30, [Guid.NewGuid()], includePlatform: true);
        state.TenantIds.Count.ShouldBe(1);

        state.Set(FeatureFlagMode.Everyone, 30, [Guid.NewGuid()], includePlatform: true);
        state.ShouldSatisfyAllConditions(s => s.Percentage.ShouldBe(0), s => s.TenantIds.ShouldBeEmpty(), s => s.IncludePlatform.ShouldBeFalse());
        Should.Throw<ArgumentOutOfRangeException>(() => state.Set(FeatureFlagMode.Targeted, 101, [], false));
    }

    [Fact]
    public void Flag_codes_and_the_catalogue_match_and_each_kind_is_described_properly()
    {
        var codes = typeof(FeatureFlagCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        codes.Order(StringComparer.Ordinal).ShouldBe(FeatureFlagCatalog.All.Select(f => f.Code).Order(StringComparer.Ordinal));
        FeatureFlagCatalog.All.ShouldAllBe(f => f.Code.StartsWith("Flag.", StringComparison.Ordinal) && f.Code.Length <= FeatureFlagState.CodeMaxLength);
        FeatureFlagCatalog.All.ShouldAllBe(f => !string.IsNullOrWhiteSpace(f.Name) && !string.IsNullOrWhiteSpace(f.Description) && !string.IsNullOrWhiteSpace(f.Owner));
        FeatureFlagCatalog.All.Where(f => f.Kind == FeatureFlagKind.Release).ShouldAllBe(f => f.RemoveBy != null, "a release flag is temporary: say when it goes");
        FeatureFlagCatalog.All.Where(f => f.Kind == FeatureFlagKind.KillSwitch).ShouldAllBe(f => f.RemoveBy == null && f.DefaultOn, "a kill switch is permanent and on until someone pulls it");
    }

    private static FeatureFlagSetting Setting(FeatureFlagMode mode, int percentage = 0, IReadOnlyList<Guid>? tenants = null, bool includePlatform = false) =>
        new(mode, percentage, tenants ?? [], includePlatform);
}
