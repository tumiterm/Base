using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;
using Base.Application.Notifications;
using Base.Application.Security;
using Base.Application.Tenancy;
using Base.Domain.Notifications;
using Base.Infrastructure.Identity;
using Base.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Subscriptions;

public sealed partial class TenantSubscriptionTests
{
    [Fact]
    public async Task A_new_tenants_administrator_gets_the_welcome_with_the_sign_in_address_plan_and_first_steps()
    {
        var planId = await CreatePlanAsync("starter", users: 5);
        await Tenants(t => t.CreateAsync(new CreateTenantRequest("Umoya Logistics", "umoya", "umoya.example.com", "zanele@umoya.test", "Zanele Khumalo", planId)));

        _app.Sent.Clear();
        await ProcessOutboxAsync();
        var welcome = _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem();
        welcome.Subject.ShouldBe("Welcome to Base: Umoya Logistics is ready");
        welcome.Body!.ShouldContain("https://umoya.example.com/account/set-password?kind=invite");
        welcome.Body!.ShouldContain("Sign in at https://umoya.example.com/account/login");
        welcome.Body!.ShouldContain("free trial of the STARTER plan until");
        welcome.Body!.ShouldContain("Invite your team");

        // Sensitive: the log keeps that it was sent, not the link.
        var logged = await _app.InScopeAsync(null, sp =>
        {
            using var system = sp.GetRequiredService<ITenantContext>().BeginSystemScope();
            return Task.FromResult(sp.GetRequiredService<AppDbContext>().NotificationMessages.Where(m => m.TemplateCode == NotificationTemplates.Tenancy.Welcome).ToList());
        });
        logged.ShouldHaveSingleItem().Body.ShouldBeNull();
    }

    [Fact]
    public async Task Tenant_details_list_its_users_and_the_welcome_can_be_resent_until_it_is_accepted()
    {
        var tenantId = (await Tenants(t => t.CreateAsync(new CreateTenantRequest("Kopano", "kopano", "kopano.example.com", "lindi@kopano.test", "Lindi Dube", null)))).GetDataOrThrow();
        (await Tenants(t => t.InviteAdministratorAsync(tenantId, "sam@kopano.test", "Sam Mokoena"))).IsSuccess.ShouldBeTrue();

        var details = (await Tenants(t => t.GetAsync(tenantId))).GetDataOrThrow();
        details.Users.Select(u => u.Email).ShouldBe(["lindi@kopano.test", "sam@kopano.test"]);
        details.Users.ShouldAllBe(u => !u.HasPassword && u.Roles.Contains(RoleNames.Administrator));

        await ProcessOutboxAsync();
        _app.Sent.Clear();
        var lindi = details.Users[0].Id;
        (await Tenants(t => t.ResendWelcomeAsync(tenantId, lindi))).IsSuccess.ShouldBeTrue();
        await ProcessOutboxAsync();
        _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem().Subject!.ShouldStartWith("Welcome to Base: Kopano");

        await _app.InScopeAsync(tenantId, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            (await users.AddPasswordAsync((await users.FindByIdAsync(lindi.ToString()))!, "a long enough passphrase")).Succeeded.ShouldBeTrue();
        });
        (await Tenants(t => t.ResendWelcomeAsync(tenantId, lindi))).ErrorType.ShouldBe(ErrorType.Conflict);

        // Another tenant's user can't be reached through this tenant.
        var platformUser = await _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var someone = new ApplicationUser { UserName = "x@platform.test", Email = "x@platform.test", DisplayName = "X" };
            (await users.CreateAsync(someone)).Succeeded.ShouldBeTrue();
            return someone.Id;
        });
        (await Tenants(t => t.ResendWelcomeAsync(tenantId, platformUser))).ErrorType.ShouldBe(ErrorType.NotFound);
    }
}
