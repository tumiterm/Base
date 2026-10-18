using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;
using Base.Application.Flags;
using Base.Domain.Flags;
using Base.Domain.Tenancy;
using Base.Infrastructure.Persistence;
using Base.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Flags;

public sealed class FeatureFlagTests(SqlServerFixture sql) : IAsyncLifetime
{
    private const string Timeline = "Flag.Timeline";
    private const string Forced = "Flag.Forced";

    private readonly TestCurrentUser _actor = new TestCurrentUser().WithAllPermissions();
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() =>
        _app = await TestApp.CreateAsync(
            sql.NewDatabase(),
            services =>
            {
                services.AddSingleton<ICurrentUser>(_actor);
                services.AddSingleton<IFeatureFlagCatalog>(new TestCatalog());
            },
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["FeatureFlags:Overrides:" + Forced] = "true" });

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task A_release_flag_starts_off_and_reaches_the_platform_and_chosen_organisations_first()
    {
        var acme = await TenantAsync("Acme");
        var other = await TenantAsync("Other");
        (await On(acme, Timeline)).ShouldBeFalse("a release flag is off until someone switches it on");

        var saved = await Flags(_app.DefaultTenantId, f => f.SetAsync(Timeline, new FeatureFlagSetting(FeatureFlagMode.Targeted, 0, [acme], IncludePlatform: true)));
        saved.Message.ShouldBe("Timeline is on for the platform's staff, 1 chosen organisation.");

        (await On(acme, Timeline)).ShouldBeTrue();
        (await On(_app.DefaultTenantId, Timeline)).ShouldBeTrue();
        (await On(other, Timeline)).ShouldBeFalse();
        (await _app.InScopeAsync(null, sp => sp.GetRequiredService<IFeatureFlags>().IsOnAsync(Timeline))).ShouldBeFalse("outside an organisation only 'everyone' counts");

        var view = (await Flags(_app.DefaultTenantId, f => f.ListAsync())).GetDataOrThrow().Single(v => v.Flag.Code == Timeline);
        view.ShouldSatisfyAllConditions(
            v => v.Source.ShouldBe(FeatureFlagSource.Saved),
            v => v.TenantsOn.ShouldBe(2),
            v => v.TenantsTotal.ShouldBe(3),
            v => v.IsStale.ShouldBeTrue("its remove-by date has passed"),
            v => v.ChangedBy.ShouldBe("Test Actor"));

        await Flags(_app.DefaultTenantId, f => f.SetAsync(Timeline, new FeatureFlagSetting(FeatureFlagMode.Everyone, 0, [], false)));
        (await On(other, Timeline)).ShouldBeTrue();
        var audited = await _app.InScopeAsync(null, sp => sp.GetRequiredService<AppDbContext>().AuditEntries.IgnoreQueryFilters()
            .CountAsync(e => e.EntityType.Contains(nameof(FeatureFlagState))));
        audited.ShouldBeGreaterThanOrEqualTo(2, "every change is in the audit trail");
    }

    [Fact]
    public async Task Configuration_forces_a_flag_and_only_platform_staff_may_change_flags()
    {
        var acme = await TenantAsync("Acme");
        (await On(acme, Forced)).ShouldBeTrue();
        await Flags(_app.DefaultTenantId, f => f.SetAsync(Forced, new FeatureFlagSetting(FeatureFlagMode.Off, 0, [], false)));
        (await On(acme, Forced)).ShouldBeTrue("configuration wins over the saved state");
        (await Flags(_app.DefaultTenantId, f => f.ListAsync())).GetDataOrThrow().Single(v => v.Flag.Code == Forced).Source.ShouldBe(FeatureFlagSource.Configuration);

        (await Flags(_app.DefaultTenantId, f => f.SetAsync(Timeline, new FeatureFlagSetting(FeatureFlagMode.Targeted, 101, [], false)))).ErrorType.ShouldBe(ErrorType.Validation);
        (await Flags(_app.DefaultTenantId, f => f.SetAsync(Timeline, new FeatureFlagSetting(FeatureFlagMode.Targeted, 0, [Guid.NewGuid()], false)))).ErrorType.ShouldBe(ErrorType.Validation);
        (await Flags(_app.DefaultTenantId, f => f.SetAsync("Flag.Unknown", new FeatureFlagSetting(FeatureFlagMode.Off, 0, [], false)))).ErrorType.ShouldBe(ErrorType.NotFound);

        _actor.Permissions.Clear();
        (await Flags(_app.DefaultTenantId, f => f.SetAsync(Timeline, new FeatureFlagSetting(FeatureFlagMode.Everyone, 0, [], false)))).ErrorType.ShouldBe(ErrorType.Forbidden);
        (await Flags(_app.DefaultTenantId, f => f.ListAsync())).ErrorType.ShouldBe(ErrorType.Forbidden);
    }

    private Task<Guid> TenantAsync(string name) => _app.InScopeAsync(null, async sp =>
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var tenant = new Tenant("t" + Guid.NewGuid().ToString("N")[..8], name);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    });

    private Task<bool> On(Guid tenantId, string code) => Flags(tenantId, f => f.IsOnAsync(code));

    private Task<T> Flags<T>(Guid tenantId, Func<IFeatureFlags, Task<T>> work) =>
        _app.InScopeAsync(tenantId, sp => work(sp.GetRequiredService<IFeatureFlags>()));

    private sealed class TestCatalog : IFeatureFlagCatalog
    {
        public IReadOnlyList<FeatureFlagDefinition> Flags { get; } =
        [
            .. FeatureFlagCatalog.All,
            new(Timeline, "Timeline", "A timeline on each person.", FeatureFlagKind.Release, "People team", DefaultOn: false, RemoveBy: new DateOnly(2025, 12, 31)),
            new(Forced, "Forced", "Forced by configuration.", FeatureFlagKind.Release, "Platform team", DefaultOn: false, RemoveBy: new DateOnly(2027, 1, 1)),
        ];

        public FeatureFlagDefinition? Find(string code) => Flags.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.Ordinal));
    }
}
