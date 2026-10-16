using System.Net;
using System.Text.RegularExpressions;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;
using Base.Application.Security;
using Base.Application.Users;
using Base.Domain.Tenancy;
using Base.Infrastructure.Outbox;
using Base.Infrastructure.Persistence;
using Base.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Base.IntegrationTests.Identity;

public sealed partial class UserAdministrationTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly TestCurrentUser _actor = new TestCurrentUser().WithAllPermissions();
    private readonly CapturingEmailSender _email = new();
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() =>
        _app = await TestApp.CreateAsync(sql.NewDatabase(), services =>
        {
            services.AddSingleton<ICurrentUser>(_actor);
            services.AddSingleton<IEmailSender>(_email);
        });

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Invite_emails_a_single_use_link_that_activates_the_account()
    {
        var userId = (await Users(u => u.InviteAsync(new("new.person@example.com", "New Person", [RoleNames.User])))).GetDataOrThrow();

        var invited = (await Users(u => u.GetAsync(userId))).GetDataOrThrow();
        invited.HasPassword.ShouldBeFalse();
        invited.Roles.ShouldBe([RoleNames.User]);

        var link = await DeliverSingleEmailLinkAsync("new.person@example.com");
        link.ShouldStartWith("https://localhost:7262/account/set-password?kind=invite&userId=" + userId);

        var request = SetPasswordFrom(link, "A-long-enough-passphrase");
        var accepted = await Accounts(a => a.SetPasswordAsync(request));
        accepted.IsSuccess.ShouldBeTrue(accepted.ToString());
        (await Users(u => u.GetAsync(userId))).GetDataOrThrow().HasPassword.ShouldBeTrue();

        var reused = await Accounts(a => a.SetPasswordAsync(request with { NewPassword = "Another-passphrase-1", ConfirmPassword = "Another-passphrase-1" }));
        reused.IsError.ShouldBeTrue();
        reused.Message!.ShouldContain("invalid, has already been used");
    }

    [Fact]
    public async Task Forgot_password_never_reveals_whether_an_account_exists()
    {
        var unknown = await Accounts(a => a.RequestPasswordResetAsync("nobody@example.com"));

        unknown.IsSuccess.ShouldBeTrue();
        await ProcessOutboxAsync();
        _email.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task Password_reset_link_works_once()
    {
        await CreateActiveUserAsync("reset.me@example.com", "Original-passphrase-1");

        (await Accounts(a => a.RequestPasswordResetAsync("reset.me@example.com"))).IsSuccess.ShouldBeTrue();
        var link = await DeliverSingleEmailLinkAsync("reset.me@example.com");
        link.ShouldContain("kind=reset");

        var request = SetPasswordFrom(link, "Brand-new-passphrase-2");
        (await Accounts(a => a.SetPasswordAsync(request))).IsSuccess.ShouldBeTrue();
        (await Accounts(a => a.SetPasswordAsync(request))).IsError.ShouldBeTrue();
    }

    [Fact]
    public async Task Weak_passwords_are_rejected_on_the_password_field()
    {
        await Users(u => u.InviteAsync(new("weak@example.com", "Weak", [])));
        var link = await DeliverSingleEmailLinkAsync("weak@example.com");

        var result = await Accounts(a => a.SetPasswordAsync(SetPasswordFrom(link, "short")));

        result.ErrorType.ShouldBe(ErrorType.Validation);
        result.Errors.ShouldContain(e => e.Field == nameof(SetPasswordRequest.NewPassword));
    }

    [Fact]
    public async Task You_cannot_assign_a_role_with_permissions_you_do_not_hold()
    {
        _actor.With(PermissionCodes.Users.Manage);

        var result = await Users(u => u.InviteAsync(new("escalate@example.com", "Escalation", [RoleNames.Administrator])));

        result.ErrorType.ShouldBe(ErrorType.Forbidden);
        result.Message!.ShouldContain(PermissionCodes.Settings.Edit);
    }

    [Fact]
    public async Task You_cannot_manage_someone_with_more_access_than_you()
    {
        var adminId = (await Users(u => u.InviteAsync(new("boss@example.com", "Boss", [RoleNames.Administrator])))).GetDataOrThrow();
        _actor.With(PermissionCodes.Users.Manage);

        (await Users(u => u.LockAsync(adminId))).ErrorType.ShouldBe(ErrorType.Forbidden);
        (await Users(u => u.UpdateAsync(adminId, new("Demoted", [])))).ErrorType.ShouldBe(ErrorType.Forbidden);
        (await Users(u => u.SendPasswordResetAsync(adminId))).ErrorType.ShouldBe(ErrorType.Forbidden);
    }

    [Fact]
    public async Task The_last_active_administrator_cannot_be_locked_or_demoted()
    {
        var first = (await Users(u => u.InviteAsync(new("admin1@example.com", "Admin One", [RoleNames.Administrator])))).GetDataOrThrow();

        (await Users(u => u.LockAsync(first))).ErrorType.ShouldBe(ErrorType.Conflict);
        (await Users(u => u.UpdateAsync(first, new("Admin One", [RoleNames.User])))).ErrorType.ShouldBe(ErrorType.Conflict);

        await Users(u => u.InviteAsync(new("admin2@example.com", "Admin Two", [RoleNames.Administrator])));
        (await Users(u => u.LockAsync(first))).IsSuccess.ShouldBeTrue();
        (await Users(u => u.GetAsync(first))).GetDataOrThrow().IsLockedOut.ShouldBeTrue();

        (await Users(u => u.UnlockAsync(first))).IsSuccess.ShouldBeTrue();
        (await Users(u => u.GetAsync(first))).GetDataOrThrow().IsLockedOut.ShouldBeFalse();
    }

    [Fact]
    public async Task You_cannot_lock_yourself()
    {
        var me = (await Users(u => u.InviteAsync(new("me@example.com", "Me", [])))).GetDataOrThrow();
        _actor.UserId = me;

        (await Users(u => u.LockAsync(me))).ErrorType.ShouldBe(ErrorType.Conflict);
    }

    [Fact]
    public async Task Duplicate_emails_are_rejected_on_the_email_field()
    {
        await Users(u => u.InviteAsync(new("twice@example.com", "Once", [])));

        var again = await Users(u => u.InviteAsync(new("TWICE@example.com", "Twice", [])));

        again.Errors.ShouldHaveSingleItem().Field.ShouldBe(nameof(InviteUserRequest.Email));
    }

    [Fact]
    public async Task Search_is_paged_case_insensitive_and_limited_to_the_tenant()
    {
        await Users(u => u.InviteAsync(new("alpha@example.com", "Alpha Tester", [])));
        await Users(u => u.InviteAsync(new("beta@example.com", "Beta Tester", [])));

        var otherTenant = await _app.InScopeAsync(null, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var tenant = new Tenant("elsewhere", "Elsewhere");
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            return tenant.Id;
        });
        await _app.InScopeAsync(otherTenant, sp => sp.GetRequiredService<IUserAdministration>().InviteAsync(new("gamma@example.com", "Gamma Tester", [])));

        var page = await Users(u => u.SearchAsync(new UserQuery("TESTER", 1, 1)));

        page.Total.ShouldBe(2);
        page.Items.ShouldHaveSingleItem().DisplayName.ShouldBe("Alpha Tester");
        page.TotalPages.ShouldBe(2);
    }

    private async Task CreateActiveUserAsync(string email, string password)
    {
        await Users(u => u.InviteAsync(new(email, email, [])));
        var link = await DeliverSingleEmailLinkAsync(email);
        (await Accounts(a => a.SetPasswordAsync(SetPasswordFrom(link, password)))).IsSuccess.ShouldBeTrue();
    }

    private async Task<string> DeliverSingleEmailLinkAsync(string to)
    {
        _email.Sent.Clear();
        await ProcessOutboxAsync();
        var message = _email.Sent.ShouldHaveSingleItem();
        message.To.ShouldBe([to]);
        return WebUtility.HtmlDecode(Href().Match(message.HtmlBody).Groups["href"].Value);
    }

    private static SetPasswordRequest SetPasswordFrom(string link, string password)
    {
        var query = QueryHelpers.ParseQuery(new Uri(link).Query);
        var kind = string.Equals(query["kind"], "invite", StringComparison.Ordinal) ? PasswordTokenKind.Invite : PasswordTokenKind.Reset;
        return new SetPasswordRequest(Guid.Parse(query["userId"]!), query["token"]!, kind, password, password);
    }

    private Task ProcessOutboxAsync() =>
        ActivatorUtilities.CreateInstance<OutboxProcessor>(_app.Services)
            .ProcessBatchAsync(_app.Services.GetRequiredService<IOptions<OutboxOptions>>().Value, CancellationToken.None);

    private Task<T> Users<T>(Func<IUserAdministration, Task<T>> work) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<IUserAdministration>()));

    private Task<T> Accounts<T>(Func<IAccountService, Task<T>> work) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<IAccountService>()));

    [GeneratedRegex("href=\"(?<href>[^\"]+)\"", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex Href();
}
