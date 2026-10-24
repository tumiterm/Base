using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Base.Application.Branding;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;
using Base.Application.Operations;
using Base.Application.Security;
using Base.Application.Settings;
using Base.Application.Subscriptions;
using Base.Application.Tenancy;
using Base.Application.Users;
using Base.Domain.Notifications;
using Base.Domain.Subscriptions;
using Base.Infrastructure.Identity;
using Base.Infrastructure.Outbox;
using Base.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Base.IntegrationTests.Subscriptions;

public sealed partial class TenantSubscriptionTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly TestCurrentUser _actor = new TestCurrentUser().WithAllPermissions();
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() =>
        _app = await TestApp.CreateAsync(sql.NewDatabase(), services => services.AddSingleton<ICurrentUser>(_actor));

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Creating_a_tenant_sets_up_its_domain_and_trial_and_invites_its_administrator_on_its_own_address()
    {
        var planId = await CreatePlanAsync("team", users: 10);

        var created = await Tenants(t => t.CreateAsync(new CreateTenantRequest("Acme Ltd", "acme", "Acme.Example.com", "owner@acme.test", "Olu Owner", planId)));
        var tenantId = created.GetDataOrThrow();
        created.Message!.ShouldContain("invitation is on its way");

        var details = (await Tenants(t => t.GetAsync(tenantId))).GetDataOrThrow();
        details.Tenant.PrimaryHost.ShouldBe("acme.example.com");
        details.Tenant.UserCount.ShouldBe(1);
        details.Tenant.ContactEmail.ShouldBe("owner@acme.test");
        details.Subscription!.Status.ShouldBe(SubscriptionStatus.Trialing);
        details.Subscription.CurrentPeriodEnd.ShouldBe(_app.Time.GetUtcNow().AddDays(14));
        details.Usage.Single(u => u.Code == LimitCodes.Users).ShouldSatisfyAllConditions(u => u.Used.ShouldBe(1), u => u.Limit.ShouldBe(10));

        _app.Sent.Clear();
        await ProcessOutboxAsync();
        var invitation = _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem();
        invitation.To.ShouldBe("owner@acme.test");
        // The tenant's address keeps the platform address's scheme and port (https://localhost:7262 in these tests).
        Href().Matches(invitation.HtmlBody!).Select(m => WebUtility.HtmlDecode(m.Groups["href"].Value))
            .ShouldContain(h => h.StartsWith("https://acme.example.com:7262/account/set-password?kind=invite", StringComparison.Ordinal));

        // The tenant's administrator holds the Administrator role, but none of the platform's permissions.
        var principal = await _app.InScopeAsync(tenantId, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var owner = await users.FindByEmailAsync("owner@acme.test");
            return await sp.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(owner!);
        });
        var granted = principal.FindAll(PermissionCatalog.ClaimType).Select(c => c.Value).ToList();
        granted.ShouldContain(PermissionCodes.Users.Manage);
        granted.ShouldContain(PermissionCodes.Subscription.View);
        granted.Where(PermissionCatalog.IsPlatform).ShouldBeEmpty();

        var platformAdmin = await _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var admin = new ApplicationUser { UserName = "ops@platform.test", Email = "ops@platform.test", DisplayName = "Ops" };
            (await users.CreateAsync(admin)).Succeeded.ShouldBeTrue();
            await users.AddToRoleAsync(admin, RoleNames.Administrator);
            return await sp.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>().CreateAsync(admin);
        });
        platformAdmin.HasClaim(PermissionCatalog.ClaimType, PermissionCodes.Platform.TenantsManage).ShouldBeTrue();
    }

    [Fact]
    public async Task A_copied_invitation_link_opens_the_users_own_tenant_and_works_there()
    {
        var gamma = (await Tenants(t => t.CreateAsync(new CreateTenantRequest("Gamma", "gamma", "gamma.example.com", "owner@gamma.test", "Gia", null)))).GetDataOrThrow();
        var owner = await UserIdAsync(gamma, "owner@gamma.test");

        var link = (await Tenants(t => t.CreateInviteLinkAsync(gamma, owner))).GetDataOrThrow();

        link.Url.ShouldStartWith($"https://gamma.example.com:7262/account/set-password?kind=invite&userId={owner}");
        var query = QueryHelpers.ParseQuery(new Uri(link.Url).Query);
        var accepted = await _app.InScopeAsync(gamma, sp => sp.GetRequiredService<IAccountService>()
            .SetPasswordAsync(new SetPasswordRequest(owner, query["token"]!, PasswordTokenKind.Invite, "A-long-enough-passphrase", "A-long-enough-passphrase")));
        accepted.IsSuccess.ShouldBeTrue(accepted.ToString());
    }

    [Fact]
    public async Task A_tenant_without_a_verified_address_gets_no_link_until_one_is_verified()
    {
        var delta = (await Tenants(t => t.CreateAsync(new CreateTenantRequest("Delta", "delta", null, "owner@delta.test", "Dee", null)))).GetDataOrThrow();
        var owner = await UserIdAsync(delta, "owner@delta.test");

        var refused = await Tenants(t => t.CreateInviteLinkAsync(delta, owner));
        refused.ErrorType.ShouldBe(ErrorType.Conflict);
        refused.Message!.ShouldContain("no verified address");

        (await Tenants(t => t.AddDomainAsync(delta, "delta.example.com"))).IsSuccess.ShouldBeTrue();
        var domain = (await Tenants(t => t.GetAsync(delta))).GetDataOrThrow().Domains.Single();
        (await Tenants(t => t.VerifyDomainAsync(delta, domain.Id))).IsSuccess.ShouldBeTrue();

        // Its own address keeps the platform address's scheme and port (https://localhost:7262 here, as in development).
        (await Tenants(t => t.CreateInviteLinkAsync(delta, owner))).GetDataOrThrow().Url.ShouldStartWith("https://delta.example.com:7262/account/set-password?kind=invite");
    }

    private Task<Guid> UserIdAsync(Guid tenantId, string email) =>
        _app.InScopeAsync(tenantId, async sp => (await sp.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!.Id);

    [Fact]
    public async Task A_tenant_administrator_can_still_assign_the_shared_administrator_role_in_their_own_tenant()
    {
        var tenantId = (await Tenants(t => t.CreateAsync(new CreateTenantRequest("Beta", "beta", null, "a@beta.test", "Ann", null)))).GetDataOrThrow();
        _actor.With(PermissionCatalog.All.Where(p => !p.IsPlatform).Select(p => p.Key).ToArray()); // what the tenant's admin holds

        var invited = await _app.InScopeAsync(tenantId, sp =>
            sp.GetRequiredService<IUserAdministration>().InviteAsync(new InviteUserRequest("b@beta.test", "Ben", [RoleNames.Administrator])));

        invited.IsSuccess.ShouldBeTrue(invited.Message);
    }

    [Fact]
    public async Task Plan_limits_stop_extra_users_while_tenants_without_a_subscription_are_unrestricted()
    {
        var planId = await CreatePlanAsync("solo", users: 1);
        var tenantId = (await Tenants(t => t.CreateAsync(new CreateTenantRequest("Solo", "solo", null, "me@solo.test", "Me", planId)))).GetDataOrThrow();

        var refused = await _app.InScopeAsync(tenantId, sp => sp.GetRequiredService<IUserAdministration>().InviteAsync(new InviteUserRequest("two@solo.test", "Two", [RoleNames.User])));
        refused.IsError.ShouldBeTrue();
        refused.Message!.ShouldContain("allows 1 of users");

        var platform = await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IUserAdministration>().InviteAsync(new InviteUserRequest("two@platform.test", "Two", [RoleNames.User])));
        platform.IsSuccess.ShouldBeTrue(platform.Message);

        // Raising the plan's limit applies to its subscribers straight away.
        var plan = (await Plans(p => p.GetAsync(planId))).GetDataOrThrow();
        var raised = await Plans(p => p.UpdateAsync(planId, Save(plan.Code, users: 2)));
        raised.IsSuccess.ShouldBeTrue(raised.Message);
        (await _app.InScopeAsync(tenantId, sp => sp.GetRequiredService<IUserAdministration>().InviteAsync(new InviteUserRequest("two@solo.test", "Two", [RoleNames.User])))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Features_the_plan_leaves_out_are_off_for_its_subscribers()
    {
        var planId = await CreatePlanAsync("plain", users: 5, branding: false);
        var tenantId = (await Tenants(t => t.CreateAsync(new CreateTenantRequest("Plain", "plain", null, "p@plain.test", "P", planId)))).GetDataOrThrow();

        var refused = await _app.InScopeAsync(tenantId, sp => sp.GetRequiredService<IBrandingService>().SaveAsync("#0f766e", "dark"));
        refused.ErrorType.ShouldBe(ErrorType.Forbidden);
        (await _app.InScopeAsync(tenantId, sp => sp.GetRequiredService<ITenantEntitlements>().HasFeatureAsync(FeatureCodes.Documents))).ShouldBeTrue();
        (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IBrandingService>().SaveAsync("#0f766e", "dark"))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task The_lifecycle_job_reminds_then_suspends_an_unpaid_trial_and_a_payment_restores_it()
    {
        var planId = await CreatePlanAsync("trial", users: 5);
        var tenantId = (await Tenants(t => t.CreateAsync(new CreateTenantRequest("Gamma", "gamma", null, "billing@gamma.test", "Gia", planId)))).GetDataOrThrow();
        await ProcessOutboxAsync();
        _app.Sent.Clear();

        _app.Time.Advance(TimeSpan.FromDays(11));
        await RunLifecycleAsync();
        await ProcessOutboxAsync();
        _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem().Subject!.ShouldContain("trial ends on");

        _app.Time.Advance(TimeSpan.FromDays(3));
        await RunLifecycleAsync();
        (await Subscription(tenantId)).Status.ShouldBe(SubscriptionStatus.PastDue);
        (await Access(tenantId)).HasAccess.ShouldBeTrue();

        _app.Time.Advance(TimeSpan.FromDays(7));
        await RunLifecycleAsync();
        (await Subscription(tenantId)).Status.ShouldBe(SubscriptionStatus.Suspended);
        (await Access(tenantId)).HasAccess.ShouldBeFalse();

        _app.Sent.Clear();
        await ProcessOutboxAsync();
        _app.Sent.On(NotificationChannel.Email).Select(m => m.Subject).ShouldBe(["Your Base subscription is now past due", "Your Base subscription is now suspended"], ignoreOrder: true);

        (await Subscriptions(s => s.RecordPaymentAsync(tenantId, "INV-1001"))).IsSuccess.ShouldBeTrue();
        var paid = await Subscription(tenantId);
        paid.Status.ShouldBe(SubscriptionStatus.Active);
        paid.CurrentPeriodEnd.ShouldBe(_app.Time.GetUtcNow().AddMonths(1));
        (await Access(tenantId)).HasAccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Domains_are_unique_can_be_reused_after_removal_and_the_platform_tenant_stays_on()
    {
        var first = (await Tenants(t => t.CreateAsync(new CreateTenantRequest("One", "one", "one.test", "a@one.test", "A", null)))).GetDataOrThrow();
        var second = (await Tenants(t => t.CreateAsync(new CreateTenantRequest("Two", "two", null, "a@two.test", "A", null)))).GetDataOrThrow();

        (await Tenants(t => t.AddDomainAsync(second, "ONE.test"))).IsError.ShouldBeTrue();
        var domain = (await Tenants(t => t.GetAsync(first))).GetDataOrThrow().Domains.ShouldHaveSingleItem();
        (await Tenants(t => t.RemoveDomainAsync(first, domain.Id))).IsSuccess.ShouldBeTrue();
        (await Tenants(t => t.AddDomainAsync(second, "one.test"))).IsSuccess.ShouldBeTrue();
        (await Tenants(t => t.GetAsync(second))).GetDataOrThrow().Domains.ShouldHaveSingleItem().IsVerified.ShouldBeFalse("a domain a tenant asks for is verified separately");

        (await Tenants(t => t.SetActiveAsync(_app.DefaultTenantId, false))).IsError.ShouldBeTrue();
        (await Tenants(t => t.SearchAsync(new TenantQuery("two")))).Items.ShouldHaveSingleItem().Name.ShouldBe("Two");
        (await Subscriptions(s => s.StartAsync(_app.DefaultTenantId, new StartSubscriptionRequest(Guid.NewGuid(), BillingCycle.Monthly)))).IsError.ShouldBeTrue();
    }

    private static SavePlanRequest Save(string code, long? users, bool branding = true) => new(
        code,
        code.ToUpperInvariant(),
        null,
        "ZAR",
        100m,
        1000m,
        14,
        0,
        new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            [FeatureCodes.Documents] = 1,
            [FeatureCodes.DataExchange] = 1,
            [FeatureCodes.CustomBranding] = branding ? 1 : 0,
            [LimitCodes.Users] = users,
            [LimitCodes.StorageMb] = null,
        });

    private async Task<Guid> CreatePlanAsync(string code, long? users, bool branding = true) =>
        (await Plans(p => p.CreateAsync(Save(code, users, branding)))).GetDataOrThrow();

    private Task RunLifecycleAsync() =>
        _app.InScopeAsync(null, async sp =>
        {
            using var system = sp.GetRequiredService<ITenantContext>().BeginSystemScope();
            await sp.GetServices<IScheduledJob>().Single(j => j.Code == "subscription-lifecycle").RunAsync(CancellationToken.None);
        });

    private async Task<SubscriptionView> Subscription(Guid tenantId) => (await Subscriptions(s => s.GetAsync(tenantId)))!;

    private Task<TenantAccess> Access(Guid tenantId) => _app.InScopeAsync(tenantId, sp => sp.GetRequiredService<ITenantEntitlements>().GetAsync());

    /// <summary>Two passes: status-change handlers queue the email, whose delivery is itself an outbox event.</summary>
    private async Task ProcessOutboxAsync()
    {
        for (var pass = 0; pass < 2; pass++)
        {
            await ActivatorUtilities.CreateInstance<OutboxProcessor>(_app.Services)
                .ProcessBatchAsync(_app.Services.GetRequiredService<IOptions<OutboxOptions>>().Value, CancellationToken.None);
        }
    }

    private Task<T> Tenants<T>(Func<ITenantAdministration, Task<T>> work) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<ITenantAdministration>()));

    private Task<T> Plans<T>(Func<IPlanAdministration, Task<T>> work) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<IPlanAdministration>()));

    private Task<T> Subscriptions<T>(Func<ISubscriptionService, Task<T>> work) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<ISubscriptionService>()));

    [GeneratedRegex("href=\"(?<href>[^\"]+)\"", RegexOptions.None, 1000)]
    private static partial Regex Href();
}
