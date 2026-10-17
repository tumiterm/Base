using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;
using Base.Application.Integrations;
using Base.Application.Operations;
using Base.Application.Security;
using Base.Application.Subscriptions;
using Base.Application.Tenancy;
using Base.Domain.Notifications;
using Base.Infrastructure.Outbox;
using Base.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Base.IntegrationTests.Integrations;

public sealed class ApiClientTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly TestCurrentUser _actor = new TestCurrentUser().WithAllPermissions();
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() =>
        _app = await TestApp.CreateAsync(sql.NewDatabase(), services => services.AddSingleton<ICurrentUser>(_actor));

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task A_key_is_shown_once_stored_as_a_hash_and_authenticates_with_the_clients_permissions()
    {
        var issued = (await Clients(c => c.CreateAsync(new SaveApiClientRequest("Billing sync", null, [PermissionCodes.People.View], 30, 50)))).GetDataOrThrow();

        issued.Key.ShouldStartWith("bk_");
        issued.Key.Length.ShouldBe(3 + 8 + 1 + 32);
        var listed = (await Clients(c => c.ListAsync())).ShouldHaveSingleItem();
        listed.MaskedKey.ShouldBe(issued.Key[..12] + "…");
        listed.State.ShouldBe(ApiClientState.Active);

        var result = await Authenticate(_app.DefaultTenantId, issued.Key);
        result.Client!.Name.ShouldBe("Billing sync");
        result.Client.Permissions.ShouldBe([PermissionCodes.People.View]);
        result.Client.RequestsPerMinute.ShouldBe(50);
        (await Authenticate(_app.DefaultTenantId, issued.Key[..^1] + (issued.Key[^1] == 'a' ? 'b' : 'a'))).Client.ShouldBeNull();
        (await Authenticate(_app.DefaultTenantId, "bk_nonsense")).Client.ShouldBeNull();
    }

    [Fact]
    public async Task Nobody_gives_a_client_more_than_they_hold_and_keys_cant_last_forever()
    {
        _actor.With(PermissionCodes.Api.ClientsManage, PermissionCodes.People.View);

        var escalate = await Clients(c => c.CreateAsync(new SaveApiClientRequest("Too much", null, [PermissionCodes.People.Manage])));
        escalate.ErrorType.ShouldBe(ErrorType.Forbidden);
        (await Clients(c => c.CreateAsync(new SaveApiClientRequest("Forever", null, [PermissionCodes.People.View], 5000)))).Errors.ShouldContain(e => e.Field == "ExpiresInDays");
        (await Clients(c => c.CreateAsync(new SaveApiClientRequest("Nothing", null, [])))).Errors.ShouldContain(e => e.Field == "Permissions");
        (await Clients(c => c.CreateAsync(new SaveApiClientRequest("Just right", null, [PermissionCodes.People.View])))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Rotating_replaces_the_key_and_revoking_stops_it_at_once()
    {
        var first = (await Clients(c => c.CreateAsync(new SaveApiClientRequest("HR feed", null, [PermissionCodes.People.View])))).GetDataOrThrow();
        (await Authenticate(_app.DefaultTenantId, first.Key)).Client.ShouldNotBeNull();

        var second = (await Clients(c => c.RotateAsync(first.ClientId, 30))).GetDataOrThrow();
        (await Authenticate(_app.DefaultTenantId, first.Key)).Client.ShouldBeNull("the old key stops working at once, despite the cache");
        (await Authenticate(_app.DefaultTenantId, second.Key)).Client.ShouldNotBeNull();

        (await Clients(c => c.RevokeAsync(first.ClientId))).IsSuccess.ShouldBeTrue();
        (await Authenticate(_app.DefaultTenantId, second.Key)).Client.ShouldBeNull();
        (await Clients(c => c.RotateAsync(first.ClientId, 30))).IsError.ShouldBeTrue();
        (await Clients(c => c.GetAsync(first.ClientId))).GetDataOrThrow().State.ShouldBe(ApiClientState.Revoked);

        _app.Time.Advance(TimeSpan.FromDays(31));
        var expiring = (await Clients(c => c.CreateAsync(new SaveApiClientRequest("Short", null, [PermissionCodes.People.View], 1)))).GetDataOrThrow();
        _app.Time.Advance(TimeSpan.FromDays(2));
        (await Authenticate(_app.DefaultTenantId, expiring.Key)).Client.ShouldBeNull("expired");
    }

    [Fact]
    public async Task Keys_only_work_in_their_own_tenant_and_only_on_plans_with_api_access()
    {
        var plan = (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IPlanAdministration>().CreateAsync(new SavePlanRequest(
            "basic", "Basic", null, "ZAR", 0, 0, 0, 0, new Dictionary<string, long?>(StringComparer.Ordinal) { [FeatureCodes.Api] = 0 })))).GetDataOrThrow();
        var tenant = (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<ITenantAdministration>().CreateAsync(
            new CreateTenantRequest("Basic Co", "basic-co", null, "a@basic.test", "A", plan)))).GetDataOrThrow();

        var theirs = (await _app.InScopeAsync(tenant, sp => sp.GetRequiredService<IApiClientAdministration>().CreateAsync(
            new SaveApiClientRequest("Their tool", null, [PermissionCodes.People.View])))).GetDataOrThrow();
        (await Authenticate(tenant, theirs.Key)).Failure.ShouldBe(ApiKeyFailure.NotInPlan);
        (await Authenticate(_app.DefaultTenantId, theirs.Key)).ShouldSatisfyAllConditions(r => r.Client.ShouldBeNull(), r => r.Failure.ShouldBe(ApiKeyFailure.Invalid));

        var platformOnly = await _app.InScopeAsync(tenant, sp => sp.GetRequiredService<IApiClientAdministration>().CreateAsync(
            new SaveApiClientRequest("Sneaky", null, [PermissionCodes.Platform.TenantsView])));
        platformOnly.Errors.ShouldContain(e => e.Code == "permission.platform");
    }

    [Fact]
    public async Task The_creator_is_reminded_once_two_weeks_before_the_key_expires()
    {
        _actor.UserId = await _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            var users = sp.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Base.Infrastructure.Identity.ApplicationUser>>();
            var user = new Base.Infrastructure.Identity.ApplicationUser { UserName = "dev@example.com", Email = "dev@example.com", DisplayName = "Dev" };
            (await users.CreateAsync(user)).Succeeded.ShouldBeTrue();
            return user.Id;
        });
        await Clients(c => c.CreateAsync(new SaveApiClientRequest("Reporting", null, [PermissionCodes.People.View], 30)));

        await RunJobAsync();
        await DeliverAsync();
        _app.Sent.All.ShouldBeEmpty();

        _app.Time.Advance(TimeSpan.FromDays(17));
        await RunJobAsync();
        await RunJobAsync();
        await DeliverAsync();
        await DeliverAsync();
        var email = _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem();
        email.To.ShouldBe("dev@example.com");
        email.Subject.ShouldBe("The API key for Reporting expires on " + _app.Time.GetUtcNow().AddDays(13).ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture));
        (await Clients(c => c.ListAsync())).Single().State.ShouldBe(ApiClientState.ExpiringSoon);
    }

    private Task<T> Clients<T>(Func<IApiClientAdministration, Task<T>> work) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<IApiClientAdministration>()));

    private Task<ApiKeyResult> Authenticate(Guid tenantId, string key) =>
        _app.InScopeAsync(tenantId, sp => sp.GetRequiredService<IApiKeyAuthenticator>().AuthenticateAsync(key));

    private Task RunJobAsync() =>
        _app.InScopeAsync(null, async sp =>
        {
            using var system = sp.GetRequiredService<ITenantContext>().BeginSystemScope();
            await sp.GetServices<IScheduledJob>().Single(j => j.Code == "api-key-expiry").RunAsync(CancellationToken.None);
        });

    private Task DeliverAsync() =>
        ActivatorUtilities.CreateInstance<OutboxProcessor>(_app.Services).ProcessBatchAsync(_app.Services.GetRequiredService<IOptions<OutboxOptions>>().Value, CancellationToken.None);
}
