using Base.Application.Common.Results;
using Base.Application.Subscriptions;
using Base.Application.Tenancy;
using Base.Domain.Subscriptions;
using Base.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Subscriptions;

public sealed partial class TenantSubscriptionTests
{
    [Fact]
    public async Task A_feature_added_since_a_plan_was_saved_waits_for_a_decision_and_follows_its_suggestion_meanwhile()
    {
        // A plan saved before most of today's catalogue existed: it only ever decided on Documents and Users.
        var planId = await _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var plan = new Plan("legacy", "Legacy", "ZAR", 100m, 1000m, 0);
            plan.SetEntitlement(FeatureCodes.Documents, 1);
            plan.SetEntitlement(LimitCodes.Users, 5);
            db.Plans.Add(plan);
            await db.SaveChangesAsync();
            return plan.Id;
        });
        var tenantId = (await Tenants(t => t.CreateAsync(new CreateTenantRequest("Legacy Co", "legacy", null, "l@legacy.test", "L", planId)))).GetDataOrThrow();

        var waiting = (await Plans(p => p.GetAsync(planId))).GetDataOrThrow();
        waiting.Undecided().Select(e => e.Code).ShouldBe(
            [FeatureCodes.DocumentGeneration, LimitCodes.StorageMb, FeatureCodes.DataExchange, FeatureCodes.CustomBranding, FeatureCodes.Messaging, FeatureCodes.BulkMessaging, LimitCodes.MessagesPerMonth, FeatureCodes.Api],
            ignoreOrder: true);

        // Until then: Standard features are in, Premium ones out, for the plan and its subscribers alike.
        waiting.ValueOf(FeatureCodes.DataExchange).ShouldBe(1);
        waiting.ValueOf(FeatureCodes.DocumentGeneration).ShouldBe(0);
        (await HasFeatureAsync(tenantId, FeatureCodes.DataExchange)).ShouldBeTrue();
        (await HasFeatureAsync(tenantId, FeatureCodes.DocumentGeneration)).ShouldBeFalse();

        (await Plans(p => p.AcceptSuggestionsAsync())).IsSuccess.ShouldBeTrue();

        var decided = (await Plans(p => p.GetAsync(planId))).GetDataOrThrow();
        decided.Undecided().ShouldBeEmpty();
        decided.ValueOf(FeatureCodes.DataExchange).ShouldBe(1);
        decided.ValueOf(FeatureCodes.DocumentGeneration).ShouldBe(0);
        (await Plans(p => p.AcceptSuggestionsAsync())).Message!.ShouldContain("already decided");
    }

    [Fact]
    public async Task The_plans_and_features_grid_changes_several_plans_at_once_and_subscribers_see_it_straight_away()
    {
        var starter = await CreatePlanAsync("starter", users: 5);
        var pro = await CreatePlanAsync("pro", users: 25);
        var tenantId = (await Tenants(t => t.CreateAsync(new CreateTenantRequest("Grid Co", "grid", null, "g@grid.test", "G", starter)))).GetDataOrThrow();
        (await HasFeatureAsync(tenantId, FeatureCodes.DocumentGeneration)).ShouldBeFalse();

        var saved = await Plans(p => p.SetEntitlementsAsync(
        [
            new(starter, FeatureCodes.DocumentGeneration, 1),
            new(pro, FeatureCodes.DocumentGeneration, 1),
            new(pro, LimitCodes.Users, null),
        ]));

        saved.Message.ShouldBe("2 plans saved. Their subscribers have the changes now.");
        (await HasFeatureAsync(tenantId, FeatureCodes.DocumentGeneration)).ShouldBeTrue();
        (await Plans(p => p.GetAsync(pro))).GetDataOrThrow().ValueOf(LimitCodes.Users).ShouldBeNull();
        (await Plans(p => p.SetEntitlementsAsync([new(starter, FeatureCodes.DocumentGeneration, 1)]))).Message.ShouldBe("Nothing changed.");

        (await Plans(p => p.SetEntitlementsAsync([new(starter, FeatureCodes.Api, 2)]))).ErrorType.ShouldBe(ErrorType.Validation);
        (await Plans(p => p.SetEntitlementsAsync([new(starter, LimitCodes.Users, -1)]))).ErrorType.ShouldBe(ErrorType.Validation);
        (await Plans(p => p.SetEntitlementsAsync([new(starter, "Feature.Nope", 1)]))).ErrorType.ShouldBe(ErrorType.Validation);
        (await Plans(p => p.SetEntitlementsAsync([new(Guid.NewGuid(), FeatureCodes.Api, 1)]))).ErrorType.ShouldBe(ErrorType.NotFound);
    }

    private Task<bool> HasFeatureAsync(Guid tenantId, string feature) =>
        _app.InScopeAsync(tenantId, sp => sp.GetRequiredService<ITenantEntitlements>().HasFeatureAsync(feature));
}
