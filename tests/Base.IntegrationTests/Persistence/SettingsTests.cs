using Base.Application.Common.Results;
using Base.Application.Settings;
using Base.Domain.Tenancy;
using Base.Infrastructure.Persistence;
using Base.Infrastructure.Startup;
using Base.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Persistence;

public sealed class SettingsTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() => _app = await TestApp.CreateAsync(sql.NewDatabase());

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Unset_settings_fall_back_to_the_catalogue_default()
    {
        var value = await _app.InScopeAsync(_app.DefaultTenantId, sp =>
            sp.GetRequiredService<IApplicationSettingService>().GetIntAsync(SettingCodes.Files.MaxUploadSizeMb));

        value.ShouldBe(10);
    }

    [Fact]
    public async Task Invalid_values_are_rejected_with_a_field_error()
    {
        var result = await _app.InScopeAsync(_app.DefaultTenantId, sp =>
            sp.GetRequiredService<IApplicationSettingService>().SetAsync(SettingCodes.Files.MaxUploadSizeMb, "100000"));

        result.ErrorType.ShouldBe(ErrorType.Validation);
        result.Errors.ShouldHaveSingleItem().Field.ShouldBe(SettingCodes.Files.MaxUploadSizeMb);
    }

    [Fact]
    public async Task Tenant_values_override_platform_values_for_that_tenant_only()
    {
        var otherTenant = await _app.InScopeAsync(null, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var tenant = new Tenant("second", "Second");
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            return tenant.Id;
        });

        (await _app.InScopeAsync(_app.DefaultTenantId, sp =>
            sp.GetRequiredService<IApplicationSettingService>().SetAsync(SettingCodes.General.ApplicationName, "Tenant One Portal"))).IsSuccess.ShouldBeTrue();

        (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IApplicationSettingService>().GetAsync(SettingCodes.General.ApplicationName)))
            .ShouldBe("Tenant One Portal");
        (await _app.InScopeAsync(otherTenant, sp => sp.GetRequiredService<IApplicationSettingService>().GetAsync(SettingCodes.General.ApplicationName)))
            .ShouldBe("Base");
    }

    [Fact]
    public async Task Seeder_never_overwrites_an_administrators_value()
    {
        await _app.InScopeAsync(_app.DefaultTenantId, sp =>
            sp.GetRequiredService<IApplicationSettingService>().SetAsync(SettingCodes.Notifications.ErrorAlertThrottleMinutes, "60"));

        await _app.InScopeAsync(null, sp =>
            sp.GetServices<IStartupTask>().OfType<SettingsSeedTask>().Single().ExecuteAsync(CancellationToken.None));

        (await _app.InScopeAsync(_app.DefaultTenantId, sp =>
            sp.GetRequiredService<IApplicationSettingService>().GetIntAsync(SettingCodes.Notifications.ErrorAlertThrottleMinutes))).ShouldBe(60);
    }
}
