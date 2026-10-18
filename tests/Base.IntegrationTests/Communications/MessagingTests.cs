using Base.Application.Common.Abstractions;
using Base.Application.Common.Results;
using Base.Application.Communications;
using Base.Application.Notifications;
using Base.Application.Operations;
using Base.Application.People;
using Base.Application.Security;
using Base.Domain.Communications;
using Base.Domain.Notifications;
using Base.Infrastructure.Identity;
using Base.Infrastructure.Outbox;
using Base.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Base.IntegrationTests.Communications;

/// <summary>Messages people write, sent through the real outbox and notification pipeline to capturing providers.</summary>
public sealed class MessagingTests(SqlServerFixture sql) : IAsyncLifetime
{
    private TestApp _app = null!;
    private Guid _admin;

    public async ValueTask InitializeAsync()
    {
        _app = await TestApp.CreateAsync(sql.NewDatabase());
        _admin = await CreateUserAsync("ada@example.com", "Ada Admin", RoleNames.Administrator);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task A_bulk_message_reaches_everyone_in_a_department_once_per_channel_in_their_own_words()
    {
        await AddPersonAsync("Thandi", "Nkosi", "finance", "thandi@example.com", "082 555 0101");
        await AddPersonAsync("Sipho", "Ndlovu", "finance", "sipho@example.com", null);
        await AddPersonAsync("Thandi", "Duplicate", "finance", null, "+27 82 555 0101");
        await AddPersonAsync("Lerato", "Molefe", "hr", "lerato@example.com", "082 555 0102");

        var draft = Draft(
            new AudienceChoice(BroadcastAudienceKind.Filtered, AudienceCodes.People, Filters: new Dictionary<string, string> { ["department"] = "finance" }),
            [NotificationChannel.Email, NotificationChannel.Sms],
            subject: "Payslips for {{FirstName}}",
            html: "<p>Hello {{FirstName}},</p><p>Your payslip is ready.</p><script>alert(1)</script>",
            sms: "Hi {{FirstName}}, your payslip is ready.");

        var reach = (await As(sp => Messaging(sp).ReachAsync(draft.Audience))).GetDataOrThrow();
        reach.People.ShouldBe(3);
        reach.WithEmail.ShouldBe(2);
        reach.WithPhone.ShouldBe(1, "the same phone number, however it is written, is texted once");
        reach.Label.ShouldBe("Everyone in People · Department: Finance");

        var id = (await As(sp => Messaging(sp).SendAsync(draft))).GetDataOrThrow();
        await DeliverAsync();
        await DeliverAsync();

        var emails = _app.Sent.On(NotificationChannel.Email).ToList();
        emails.Select(e => e.To).ShouldBe(["thandi@example.com", "sipho@example.com"], ignoreOrder: true);
        var thandi = emails.Single(e => e.To == "thandi@example.com");
        thandi.Subject.ShouldBe("Payslips for Thandi");
        thandi.HtmlBody!.ShouldContain("Hello Thandi");
        thandi.HtmlBody!.ShouldNotContain("<script", Case.Insensitive);
        thandi.Body!.ShouldContain("Your payslip is ready.");
        _app.Sent.On(NotificationChannel.Sms).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            s => s.To.ShouldBe("+27825550101"),
            s => s.Body.ShouldBe("Hi Thandi, your payslip is ready."));

        var details = (await As(sp => Messaging(sp).GetAsync(id))).GetDataOrThrow();
        details.Summary.Status.ShouldBe(BroadcastStatus.Sent);
        details.Summary.RecipientCount.ShouldBe(3);
        details.Delivery.ShouldBe(new DeliveryCounts(0, 3, 0, 2), "Sipho has no phone and the second Thandi record no email: skipped, with the reason");
        var skipped = (await As(sp => Messaging(sp).DeliveriesAsync(id, NotificationStatus.Skipped, 1))).Items;
        skipped.ShouldContain(d => d.Name == "Sipho Ndlovu" && d.Error!.Contains("no Sms address", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_scheduled_message_waits_for_its_time_and_a_cancelled_one_never_goes()
    {
        var person = await AddPersonAsync("Bongani", "Mokoena", "sales", "bongani@example.com", null);
        var chosen = new AudienceChoice(BroadcastAudienceKind.Chosen, AudienceCodes.People, Keys: [person.ToString()]);
        var later = _app.Time.GetUtcNow().AddHours(2);

        var scheduled = (await As(sp => Messaging(sp).SendAsync(Draft(chosen, [NotificationChannel.Email], sendAt: later)))).GetDataOrThrow();
        var cancelled = (await As(sp => Messaging(sp).SendAsync(Draft(chosen, [NotificationChannel.Email], sendAt: later)))).GetDataOrThrow();
        (await As(sp => Messaging(sp).CancelAsync(cancelled))).IsSuccess.ShouldBeTrue();
        (await As(sp => Messaging(sp).SendAsync(Draft(chosen, [NotificationChannel.Email], sendAt: _app.Time.GetUtcNow().AddSeconds(5))))).ErrorType.ShouldBe(ErrorType.Validation);

        await RunScheduledAsync();
        await DeliverAsync();
        _app.Sent.All.ShouldBeEmpty("nothing is due yet");

        _app.Time.Advance(TimeSpan.FromHours(3));
        (await RunScheduledAsync()).ShouldBe("Sent 1 message(s)");
        await DeliverAsync();

        _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem().To.ShouldBe("bongani@example.com");
        (await As(sp => Messaging(sp).GetAsync(scheduled))).GetDataOrThrow().Summary.Status.ShouldBe(BroadcastStatus.Sent);
        (await As(sp => Messaging(sp).GetAsync(cancelled))).GetDataOrThrow().Summary.Status.ShouldBe(BroadcastStatus.Cancelled);
        (await As(sp => Messaging(sp).CancelAsync(scheduled))).ErrorType.ShouldBe(ErrorType.Conflict);
    }

    [Fact]
    public async Task In_app_messages_go_to_users_by_role_and_a_pasted_list_fills_its_own_columns()
    {
        await CreateUserAsync("bob@example.com", "Bob Basic", RoleNames.User);
        var admins = new AudienceChoice(BroadcastAudienceKind.Filtered, AudienceCodes.Users, Filters: new Dictionary<string, string> { ["role"] = RoleNames.Administrator });
        (await As(sp => Messaging(sp).SendAsync(Draft(admins, [NotificationChannel.InApp], subject: "Board meeting", html: "<p>Friday at ten.</p>")))).IsSuccess.ShouldBeTrue();

        var list = new AudienceChoice(BroadcastAudienceKind.List, AudienceCodes.List, ListText: "Name,Email,Student number\nAmy Adams,amy@example.com,S-1001\nNo Address,,S-1002\n");
        var reach = (await As(sp => Messaging(sp).ReachAsync(list))).GetDataOrThrow();
        reach.People.ShouldBe(1);
        reach.Problems.ShouldHaveSingleItem().ShouldContain("Row 3");
        reach.Fields.Select(f => f.Name).ShouldContain("StudentNumber");
        (await As(sp => Messaging(sp).SendAsync(Draft(list, [NotificationChannel.Email], subject: "Results", html: "<p>Student {{StudentNumber}}: results are out.</p>")))).IsSuccess.ShouldBeTrue();

        await DeliverAsync();
        await DeliverAsync();

        var inbox = await As(sp => sp.GetRequiredService<INotificationInbox>().GetAsync(new Application.Common.Persistence.PageRequest()));
        inbox.Items.ShouldHaveSingleItem().Title.ShouldBe("Board meeting");
        _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem().HtmlBody!.ShouldContain("Student S-1001: results are out.");
    }

    [Fact]
    public async Task Wording_permissions_and_tests_are_checked_before_anything_is_sent()
    {
        var person = await AddPersonAsync("Zola", "Dube", "it", "zola@example.com", null);
        var chosen = new AudienceChoice(BroadcastAudienceKind.Chosen, AudienceCodes.People, Keys: [person.ToString()]);

        var unknown = await As(sp => Messaging(sp).SendAsync(Draft(chosen, [NotificationChannel.Email], html: "<p>Hi {{Nickname}}</p>")));
        unknown.ErrorType.ShouldBe(ErrorType.Validation);
        unknown.Errors.ShouldContain(e => e.Message.Contains("{{Nickname}}", StringComparison.Ordinal));
        (await As(sp => Messaging(sp).SendAsync(Draft(chosen, [NotificationChannel.WhatsApp])))).Errors.ShouldContain(e => e.Field == nameof(MessageDraft.WhatsAppTemplateName));

        var preview = (await As(sp => Messaging(sp).PreviewAsync(Draft(chosen, [NotificationChannel.Email, NotificationChannel.Sms], sms: "Hi {{FirstName}}")))).GetDataOrThrow();
        preview.Sms.ShouldBe("Hi Zola");
        preview.SmsParts.ShouldBe(1);

        (await As(sp => Messaging(sp).SendTestAsync(Draft(chosen, [NotificationChannel.Email])))).IsSuccess.ShouldBeTrue();
        await DeliverAsync();
        _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem().To.ShouldBe("ada@example.com", "a test goes to the sender only");

        // Someone who may send messages, but not in bulk, can still write to people they pick.
        var sender = await CreateUserAsync("sam@example.com", "Sam Sender", null);
        await GrantAsync(sender, PermissionCodes.Messages.Send);
        await GrantAsync(sender, PermissionCodes.People.View);
        var everyone = new AudienceChoice(BroadcastAudienceKind.Filtered, AudienceCodes.People);
        (await As(sender, sp => Messaging(sp).SendAsync(Draft(everyone, [NotificationChannel.Email])))).ErrorType.ShouldBe(ErrorType.Forbidden);
        (await As(sender, sp => Messaging(sp).SendAsync(Draft(chosen, [NotificationChannel.Email])))).IsSuccess.ShouldBeTrue();
    }

    private static MessageDraft Draft(AudienceChoice audience, IReadOnlyList<NotificationChannel> channels, string subject = "Hello", string html = "<p>Hello {{FirstName}}</p>", string? sms = null, DateTimeOffset? sendAt = null) =>
        new("Test message", channels, subject, html, sms, null, null, null, audience, sendAt);

    private static IMessagingService Messaging(IServiceProvider sp) => sp.GetRequiredService<IMessagingService>();

    private Task<Guid> AddPersonAsync(string first, string last, string department, string? email, string? phone) =>
        As(async sp => (await sp.GetRequiredService<IPersonService>().CreateAsync(new SavePersonRequest(first, last, Email: email, Phone: phone, DepartmentCode: department))).GetDataOrThrow());

    private Task<T> As<T>(Func<IServiceProvider, Task<T>> work) => As(_admin, work);

    private Task<T> As<T>(Guid userId, Func<IServiceProvider, Task<T>> work) =>
        _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            using var acting = await sp.GetRequiredService<IRunAsUser>().BeginAsync(userId) ?? throw new InvalidOperationException("Can't act as the user.");
            return await work(sp);
        });

    private Task DeliverAsync() =>
        ActivatorUtilities.CreateInstance<OutboxProcessor>(_app.Services).ProcessBatchAsync(_app.Services.GetRequiredService<IOptions<OutboxOptions>>().Value, CancellationToken.None);

    private Task<string> RunScheduledAsync() =>
        _app.InScopeAsync(null, sp => sp.GetServices<IScheduledJob>().Single(j => j.Code == "scheduled-messages").RunAsync(CancellationToken.None));

    private Task<Guid> CreateUserAsync(string email, string name, string? role) =>
        _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = name };
            (await users.CreateAsync(user, "Correct-Horse-Battery-Staple")).Succeeded.ShouldBeTrue();
            if (role is not null)
            {
                (await users.AddToRoleAsync(user, role)).Succeeded.ShouldBeTrue();
            }

            return user.Id;
        });

    private Task GrantAsync(Guid userId, string permission) =>
        _app.InScopeAsync(_app.DefaultTenantId, async sp =>
        {
            var roles = sp.GetRequiredService<RoleManager<ApplicationRole>>();
            var role = new ApplicationRole("Custom " + Guid.NewGuid().ToString("N")[..6]) { Id = Domain.Common.SequentialGuid.Next() };
            (await roles.CreateAsync(role)).Succeeded.ShouldBeTrue();
            await roles.AddClaimAsync(role, new System.Security.Claims.Claim(PermissionCatalog.ClaimType, permission));
            var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
            (await users.AddToRoleAsync((await users.FindByIdAsync(userId.ToString()))!, role.Name!)).Succeeded.ShouldBeTrue();
        });
}
