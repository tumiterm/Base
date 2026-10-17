using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;
using Base.Application.Operations;
using Base.Application.Releases;
using Base.Application.Settings;
using Base.Application.Subscriptions;
using Base.Application.Tenancy;
using Base.Domain.Subscriptions;
using Base.Domain.Tenancy;
using Base.Infrastructure.Persistence;
using Base.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Releases;

public sealed class ReleaseAdoptionTests(SqlServerFixture sql) : IAsyncLifetime
{
    private const string Timeline = "Release.Timeline";
    private const string Search = "Release.Search";
    private const string Approvals = "Release.Approvals";
    private const string Exports = "Release.ScheduledExports";

    private readonly TestCurrentUser _actor = new TestCurrentUser().WithAllPermissions();
    private readonly TestCatalog _catalog = new();
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() =>
        _app = await TestApp.CreateAsync(sql.NewDatabase(), services =>
        {
            services.AddSingleton<ICurrentUser>(_actor);
            services.AddSingleton<IReleaseCatalog>(_catalog);
        });

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task New_tenants_start_up_to_date_and_older_ones_see_what_is_waiting()
    {
        var created = (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<ITenantAdministration>()
            .CreateAsync(new CreateTenantRequest("Fresh", "fresh", null, "a@fresh.test", "Ann", null)))).GetDataOrThrow();
        var fresh = await State(created);
        fresh.ShouldSatisfyAllConditions(s => s.IsUpToDate.ShouldBeTrue(), s => s.EffectiveVersion.ShouldBe("1.3"), s => s.AdoptedVersion.ShouldBe("1.3"));
        (await On(created, Timeline)).ShouldBeTrue();

        var old = await OldTenantAsync("1.0.0");
        var state = await State(old);
        state.Pending.Select(p => p.Version).ShouldBe(["1.1", "1.2", "1.3"]);
        state.EffectiveVersion.ShouldBe("1.0");
        (await On(old, Timeline)).ShouldBeFalse("opt-in features wait for the release");
        (await On(old, Search)).ShouldBeTrue("on-by-default features arrive at once");
    }

    [Fact]
    public async Task Features_can_be_tried_early_kept_off_and_handed_back_to_the_release()
    {
        var old = await OldTenantAsync("1.0.0");

        (await Releases(old, r => r.ChooseAsync(Timeline, true))).IsSuccess.ShouldBeTrue();
        (await Releases(old, r => r.ChooseAsync(Search, false))).IsSuccess.ShouldBeTrue();
        (await On(old, Timeline)).ShouldBeTrue();
        (await On(old, Search)).ShouldBeFalse();
        var state = await State(old);
        state.OnEarly.ShouldBe(1);
        state.Pending.SelectMany(p => p.Features).Single(f => f.Feature.Code == Timeline).Source.ShouldBe(FeatureStateSource.Choice);

        (await Releases(old, r => r.ClearChoiceAsync(Timeline))).IsSuccess.ShouldBeTrue();
        (await On(old, Timeline)).ShouldBeFalse();
        (await Releases(old, r => r.ChooseAsync("Release.Unknown", true))).ErrorType.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public async Task Taking_a_release_switches_its_features_on_and_drops_earlier_choices_about_them()
    {
        var old = await OldTenantAsync("1.0.0");
        await Releases(old, r => r.ChooseAsync(Search, false));
        await Releases(old, r => r.ChooseAsync(Exports, true));

        var adopted = await Releases(old, r => r.AdoptAsync("1.2"));
        adopted.Message.ShouldBe("You're on 1.2: 3 new features are on.");

        var state = await State(old);
        state.Pending.Select(p => p.Version).ShouldBe(["1.3"]);
        (await On(old, Timeline)).ShouldBeTrue();
        (await On(old, Search)).ShouldBeTrue("the choice to keep the old way went with the release");
        (await On(old, Exports)).ShouldBeTrue("a choice about a later release stays");

        (await Releases(old, r => r.AdoptAsync("2.0"))).IsSuccess.ShouldBeFalse();
        (await Releases(old, r => r.AdoptAsync())).IsSuccess.ShouldBeTrue();
        (await State(old)).IsUpToDate.ShouldBeTrue();
    }

    [Fact]
    public async Task A_feature_outside_the_plan_stays_off_and_cant_be_chosen()
    {
        var old = await OldTenantAsync("1.0.0");
        var plan = (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IPlanAdministration>().CreateAsync(new SavePlanRequest(
            "basic", "Basic", null, "ZAR", 10m, 100m, 0, 0,
            new Dictionary<string, long?>(StringComparer.Ordinal)
            {
                [FeatureCodes.Documents] = 0,
                [FeatureCodes.DataExchange] = 1,
                [FeatureCodes.CustomBranding] = 1,
                [LimitCodes.Users] = null,
                [LimitCodes.StorageMb] = null,
            })))).GetDataOrThrow();
        (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<ISubscriptionService>()
            .StartAsync(old, new StartSubscriptionRequest(plan, BillingCycle.Monthly, WithTrial: false)))).IsSuccess.ShouldBeTrue();

        (await Releases(old, r => r.AdoptAsync())).IsSuccess.ShouldBeTrue();
        (await On(old, Approvals)).ShouldBeFalse();
        (await Releases(old, r => r.ChooseAsync(Approvals, true))).ErrorType.ShouldBe(ErrorType.Conflict);
    }

    [Fact]
    public async Task The_support_window_moves_tenants_that_fall_too_far_behind_and_the_platform_sees_the_spread()
    {
        var far = await OldTenantAsync("1.0.0");
        var near = await OldTenantAsync("1.2.0");
        (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IApplicationSettingService>().SetAsync(SettingCodes.Releases.MaxReleasesBehind, "1"))).IsSuccess.ShouldBeTrue();

        var result = await _app.InScopeAsync(null, async sp =>
        {
            using var system = sp.GetRequiredService<ITenantContext>().BeginSystemScope();
            return await sp.GetServices<IScheduledJob>().Single(j => j.Code == "release-window").RunAsync(CancellationToken.None);
        });
        result.ShouldBe("Moved 1 organisation(s) forward to stay within 1 release(s)");
        (await State(far)).ShouldSatisfyAllConditions(s => s.AdoptedVersion.ShouldBe("1.2"), s => s.Behind.ShouldBe(1));
        (await State(near)).Behind.ShouldBe(1);

        var fleet = await Releases(_app.DefaultTenantId, r => r.FleetAsync());
        fleet.CurrentVersion.ShouldBe("1.3");
        fleet.Versions.Single(v => v.Version == "1.2").Tenants.ShouldBe(2);
        fleet.Features.Single(f => f.Feature.Code == Search).OnCount.ShouldBe(fleet.TenantCount);
    }

    private async Task<Guid> OldTenantAsync(string version) => await _app.InScopeAsync(null, async sp =>
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var tenant = new Tenant("t" + Guid.NewGuid().ToString("N")[..8], "Older " + version);
        tenant.AdoptRelease(version, _app.Time.GetUtcNow());
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    });

    private Task<TenantReleaseState> State(Guid tenantId) => Releases(tenantId, r => r.GetAsync());

    private Task<bool> On(Guid tenantId, string code) => Releases(tenantId, r => r.IsOnAsync(code));

    private Task<T> Releases<T>(Guid tenantId, Func<ITenantReleases, Task<T>> work) =>
        _app.InScopeAsync(tenantId, sp => work(sp.GetRequiredService<ITenantReleases>()));

    private sealed class TestCatalog : IReleaseCatalog
    {
        public string CurrentVersion => "1.3.0";

        public IReadOnlyList<ReleaseFeature> Features { get; } =
        [
            new(Timeline, "Timeline", "A timeline on each person.", "1.1.0"),
            new(Search, "Search", "Search across records.", "1.2.0", ReleaseRollout.OnByDefault),
            new(Approvals, "Approvals", "Approve documents.", "1.2.0", PlanFeature: FeatureCodes.Documents),
            new(Exports, "Scheduled exports", "Exports on a schedule.", "1.3.0"),
            new("Release.Future", "Future", "Not shipped yet.", "1.4.0"),
        ];
    }
}
