using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;
using Base.Application.Notifications;
using Base.Application.Settings;
using Base.Domain.Notifications;
using Base.Domain.Settings;
using Base.Domain.Tenancy;
using Base.Infrastructure.Persistence;
using Base.Infrastructure.Startup;
using Base.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task Platform_settings_can_only_be_changed_from_the_platform_tenant()
    {
        var other = await NewTenantAsync("other");

        var refused = await Settings(other, s => s.SetAsync(SettingCodes.Exports.MaxRows, "5"));
        refused.ErrorType.ShouldBe(ErrorType.Forbidden);
        (await Settings(other, s => s.SetManyAsync(Values((SettingCodes.General.ApplicationName, "Mine"), (SettingCodes.Email.SmtpHost, "smtp.evil.test")))))
            .ErrorType.ShouldBe(ErrorType.Forbidden);
        (await Settings(other, s => s.GetAsync(SettingCodes.General.ApplicationName))).ShouldBe("Base");
        (await Settings(other, s => s.CanEditPlatformAsync())).ShouldBeFalse();
        (await Settings(_app.DefaultTenantId, s => s.SetAsync(SettingCodes.Email.SmtpHost, "smtp.example.com"))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Saving_a_form_is_all_or_nothing_and_leaves_inherited_values_alone()
    {
        var other = await NewTenantAsync("other");

        (await Settings(other, s => s.SetManyAsync(Values((SettingCodes.General.ApplicationName, "Base"), (SettingCodes.Files.MaxUploadSizeMb, "10")))))
            .Message.ShouldBe("Nothing changed.");
        (await TenantRowsAsync(other)).ShouldBeEmpty();

        var invalid = await Settings(other, s => s.SetManyAsync(Values((SettingCodes.General.ApplicationName, "Other Portal"), (SettingCodes.Files.MaxUploadSizeMb, "100000"))));
        invalid.ErrorType.ShouldBe(ErrorType.Validation);
        invalid.Errors.ShouldHaveSingleItem().Field.ShouldBe(SettingCodes.Files.MaxUploadSizeMb);
        (await Settings(other, s => s.GetAsync(SettingCodes.General.ApplicationName))).ShouldBe("Base");

        (await Settings(other, s => s.SetManyAsync(Values((SettingCodes.General.ApplicationName, "Other Portal"), (SettingCodes.Security.PasswordResetEnabled, "False"))))).IsSuccess.ShouldBeTrue();
        (await Settings(other, s => s.GetBoolAsync(SettingCodes.Security.PasswordResetEnabled))).ShouldBeFalse();
        var all = await Settings(other, s => s.GetAllAsync());
        all.Single(v => v.Definition.Code == SettingCodes.General.ApplicationName).ShouldSatisfyAllConditions(v => v.IsChanged.ShouldBeTrue(), v => v.IsTenantOverride.ShouldBeTrue());

        // Reverting a tenant's change brings back the inherited value.
        (await Settings(other, s => s.RevertAsync(SettingCodes.General.ApplicationName))).IsSuccess.ShouldBeTrue();
        (await Settings(other, s => s.GetAsync(SettingCodes.General.ApplicationName))).ShouldBe("Base");
        (await Settings(other, s => s.GetAllAsync())).Single(v => v.Definition.Code == SettingCodes.General.ApplicationName).IsChanged.ShouldBeFalse();
    }

    [Fact]
    public async Task Secret_settings_are_encrypted_never_listed_and_kept_out_of_the_audit_trail()
    {
        const string Password = "correct horse battery staple";
        (await Settings(_app.DefaultTenantId, s => s.SetAsync(SettingCodes.Email.SmtpPassword, Password))).IsSuccess.ShouldBeTrue();

        (await Settings(_app.DefaultTenantId, s => s.GetAsync(SettingCodes.Email.SmtpPassword))).ShouldBe(Password);
        var listed = (await Settings(_app.DefaultTenantId, s => s.GetAllAsync())).Single(v => v.Definition.Code == SettingCodes.Email.SmtpPassword);
        listed.ShouldSatisfyAllConditions(v => v.Value.ShouldBeNull(), v => v.HasSecret.ShouldBeTrue(), v => v.IsChanged.ShouldBeTrue());

        var (row, audit) = await _app.InScopeAsync(null, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            using var system = sp.GetRequiredService<ITenantContext>().BeginSystemScope();
            var stored = await db.ApplicationSettings.SingleAsync(r => r.Code == SettingCodes.Email.SmtpPassword && r.TenantId == null);
            var entries = await db.AuditEntries.Where(a => a.EntityType == nameof(ApplicationSetting)).Select(a => a.Changes).ToListAsync();
            return (stored, entries);
        });
        row.Value.ShouldBeNull();
        row.ProtectedValue.ShouldNotBeNull().ShouldNotContain(Password);
        audit.ShouldNotBeEmpty();
        audit.ShouldAllBe(changes => !changes.Contains(Password) && !changes.Contains(row.ProtectedValue!));

        // A blank form field means "keep it"; removing it is a revert.
        (await Settings(_app.DefaultTenantId, s => s.RevertAsync(SettingCodes.Email.SmtpPassword))).IsSuccess.ShouldBeTrue();
        (await Settings(_app.DefaultTenantId, s => s.GetAsync(SettingCodes.Email.SmtpPassword))).ShouldBeNull();
    }

    [Fact]
    public async Task Email_goes_through_the_smtp_server_from_settings_once_it_is_complete_and_switched_on()
    {
        (await Settings(_app.DefaultTenantId, s => s.SetAsync(SettingCodes.Email.SmtpEnabled, "true"))).IsSuccess.ShouldBeTrue();
        var incomplete = await Channels(c => c.DescribeAsync(NotificationChannel.Email));
        incomplete.Source.ShouldBe(ChannelRouteSource.Configuration);

        (await Settings(_app.DefaultTenantId, s => s.SetManyAsync(Values(
            (SettingCodes.Email.SmtpHost, "smtp.example.com"), (SettingCodes.Email.SmtpPort, "2525"), (SettingCodes.Email.FromAddress, "noreply@example.com"))))).IsSuccess.ShouldBeTrue();

        var route = await Channels(c => c.DescribeAsync(NotificationChannel.Email));
        route.ShouldSatisfyAllConditions(
            r => r.Source.ShouldBe(ChannelRouteSource.EmailSettings),
            r => r.Delivers.ShouldBeTrue(),
            r => r.ProviderName.ShouldBe("smtp-settings"),
            r => r.Description.ShouldBe("Sent through smtp.example.com:2525."));
        (await Channels(c => c.ResolveAsync(NotificationChannel.Email)))!.Name.ShouldBe("smtp-settings");
        (await Settings(_app.DefaultTenantId, s => s.SetAsync(SettingCodes.Email.FromAddress, "not an address"))).ErrorType.ShouldBe(ErrorType.Validation);
    }

    private static Dictionary<string, string?> Values(params (string Code, string? Value)[] values) =>
        values.ToDictionary(v => v.Code, v => v.Value, StringComparer.Ordinal);

    private Task<T> Settings<T>(Guid tenantId, Func<IApplicationSettingService, Task<T>> work) =>
        _app.InScopeAsync(tenantId, sp => work(sp.GetRequiredService<IApplicationSettingService>()));

    private Task<T> Channels<T>(Func<INotificationChannels, Task<T>> work) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<INotificationChannels>()));

    private Task<Guid> NewTenantAsync(string code) => _app.InScopeAsync(null, async sp =>
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var tenant = new Tenant(code, code);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    });

    private Task<List<ApplicationSetting>> TenantRowsAsync(Guid tenantId) => _app.InScopeAsync(null, sp =>
        sp.GetRequiredService<AppDbContext>().ApplicationSettings.IgnoreQueryFilters().Where(r => r.TenantId == tenantId).ToListAsync());
}
