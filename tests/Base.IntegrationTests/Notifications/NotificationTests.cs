using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Notifications;
using Base.Application.Security;
using Base.Application.Users;
using Base.Domain.Notifications;
using Base.Domain.Tenancy;
using Base.Infrastructure.Identity;
using Base.Infrastructure.Notifications;
using Base.Infrastructure.Outbox;
using Base.Infrastructure.Persistence;
using Base.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Base.IntegrationTests.Notifications;

public sealed class NotificationTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly TestCurrentUser _actor = new TestCurrentUser().WithAllPermissions();
    private TestApp _app = null!;

    public async ValueTask InitializeAsync() =>
        _app = await TestApp.CreateAsync(sql.NewDatabase(), services => services.AddSingleton<ICurrentUser>(_actor));

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Email_and_in_app_are_delivered_after_commit_and_the_inbox_tracks_reads()
    {
        var userId = await CreateUserAsync("ann@example.com", "Ann Adams");

        var ids = (await Notify(new Notification(NotificationTemplates.Identity.PasswordChanged, NotificationRecipient.ToUser(userId)))).GetDataOrThrow();
        ids.Count.ShouldBe(2);
        _app.Sent.All.ShouldBeEmpty("nothing is sent before the outbox runs");

        await DeliverAsync();

        var email = _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem();
        email.To.ShouldBe("ann@example.com");
        email.Subject.ShouldBe("Your Base password was changed");
        email.HtmlBody!.ShouldContain("Hello Ann Adams");
        email.HtmlBody!.ShouldContain("https://localhost:7262/account/forgot-password");

        _actor.UserId = userId;
        (await Inbox(i => i.UnreadCountAsync())).ShouldBe(1);
        var item = (await Inbox(i => i.GetAsync(new PageRequest()))).Items.ShouldHaveSingleItem();
        item.Title.ShouldBe("Your password was changed");
        (await Inbox(i => i.MarkReadAsync(item.Id))).IsSuccess.ShouldBeTrue();
        (await Inbox(i => i.UnreadCountAsync())).ShouldBe(0);
    }

    [Fact]
    public async Task A_permanent_whatsapp_failure_falls_back_to_sms()
    {
        var userId = await CreateUserAsync("bob@example.com", "Bob", phone: "082 123 4567");
        _app.Sent.FailNext(NotificationChannel.WhatsApp, "131026 Message undeliverable", retryable: false);

        await Notify(Reminder(userId));
        await DeliverAsync(); // WhatsApp fails, SMS is queued
        _app.Sent.Failures.Clear();
        await DeliverAsync(); // SMS goes out

        var sms = _app.Sent.On(NotificationChannel.Sms).ShouldHaveSingleItem();
        sms.To.ShouldBe("+27821234567");
        sms.Body.ShouldBe("Base: Pay rent. Due on Friday.");

        var log = await Messages(userId);
        var whatsapp = log.Single(m => m.Channel == NotificationChannel.WhatsApp);
        whatsapp.Status.ShouldBe(NotificationStatus.Failed);
        whatsapp.LastError.ShouldBe("131026 Message undeliverable");
        log.Single(m => m.Channel == NotificationChannel.Sms).FallbackOf.ShouldBe(whatsapp.Id);
        log.ShouldNotContain(m => m.Channel == NotificationChannel.Email, "email is only the last resort");
    }

    [Fact]
    public async Task Retryable_failures_are_retried_then_fall_back()
    {
        var userId = await CreateUserAsync("cat@example.com", "Cat", phone: "+27831112222");
        _app.Sent.FailNext(NotificationChannel.WhatsApp, "503 Service Unavailable", retryable: true);

        await Notify(Reminder(userId));
        for (var attempt = 0; attempt < NotificationDispatcher.MaxAttempts; attempt++)
        {
            await DeliverAsync();
            _app.Time.Advance(TimeSpan.FromHours(1)); // past the outbox backoff
        }

        var whatsapp = (await Messages(userId)).Single(m => m.Channel == NotificationChannel.WhatsApp);
        whatsapp.Status.ShouldBe(NotificationStatus.Failed);
        whatsapp.Attempts.ShouldBe(NotificationDispatcher.MaxAttempts);

        await DeliverAsync();
        _app.Sent.On(NotificationChannel.Sms).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Channels_without_an_address_are_skipped_with_a_reason()
    {
        var userId = await CreateUserAsync("dan@example.com", "Dan"); // no phone

        await Notify(Reminder(userId));
        await DeliverAsync();

        var log = await Messages(userId);
        log.Single(m => m.Channel == NotificationChannel.WhatsApp).LastError.ShouldBe("The recipient has no WhatsApp address.");
        log.Single(m => m.Channel == NotificationChannel.Sms).Status.ShouldBe(NotificationStatus.Skipped);
        log.Single(m => m.Channel == NotificationChannel.Email).Status.ShouldBe(NotificationStatus.Sent);
        _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem().Subject.ShouldBe("Reminder: Pay rent");
    }

    [Fact]
    public async Task Opt_outs_are_respected_but_security_messages_always_go()
    {
        var userId = await CreateUserAsync("eve@example.com", "Eve", phone: "0821230000");
        _actor.UserId = userId;
        (await Preferences(p => p.SetMineAsync(NotificationCategories.Reminders, NotificationChannel.WhatsApp, false))).IsSuccess.ShouldBeTrue();
        (await Preferences(p => p.SetMineAsync(NotificationCategories.Security, NotificationChannel.Email, false))).ErrorType.ShouldBe(ErrorType.Conflict);

        await Notify(Reminder(userId));
        await DeliverAsync();

        _app.Sent.On(NotificationChannel.WhatsApp).ShouldBeEmpty();
        _app.Sent.On(NotificationChannel.Sms).ShouldHaveSingleItem();
        (await Messages(userId)).Single(m => m.Channel == NotificationChannel.WhatsApp).LastError!.ShouldContain("opted out");

        var mine = await Preferences(p => p.GetMineAsync());
        mine.Single(p => p.Category == NotificationCategories.Reminders && p.Channel == NotificationChannel.WhatsApp).IsEnabled.ShouldBeFalse();
        mine.Where(p => p.Category == NotificationCategories.Security).ShouldAllBe(p => p.IsLocked && p.IsEnabled);
    }

    [Fact]
    public async Task Switched_off_channels_are_skipped()
    {
        await using var app = await TestApp.CreateAsync(sql.NewDatabase(), s => s.AddSingleton<ICurrentUser>(_actor),
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["Notifications:Channels:WhatsApp:Enabled"] = "false" });
        var userId = await app.InScopeAsync(app.DefaultTenantId, sp => CreateUserAsync(sp, "fay@example.com", "Fay", "0821239999"));

        await app.InScopeAsync(app.DefaultTenantId, sp => sp.GetRequiredService<INotificationService>().SendAsync(Reminder(userId)));
        await app.InScopeAsync(null, sp => ActivatorUtilities.CreateInstance<OutboxProcessor>(app.Services).ProcessBatchAsync(app.Services.GetRequiredService<IOptions<OutboxOptions>>().Value, CancellationToken.None));

        app.Sent.On(NotificationChannel.WhatsApp).ShouldBeEmpty();
        app.Sent.On(NotificationChannel.Sms).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Sensitive_messages_are_sent_at_once_and_keep_no_content()
    {
        await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IUserAdministration>().InviteAsync(new("gus@example.com", "Gus", [])));
        await DeliverAsync();

        var link = _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem().HtmlBody!;
        link.ShouldContain("/account/set-password?kind=invite");

        var stored = await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<AppDbContext>().NotificationMessages
            .SingleAsync(m => m.TemplateCode == NotificationTemplates.Identity.UserInvited));
        stored.Status.ShouldBe(NotificationStatus.Sent);
        stored.IsSensitive.ShouldBeTrue();
        new[] { stored.Subject, stored.Body, stored.HtmlBody, stored.DataJson, stored.RecipientJson }.ShouldAllBe(v => v == null);

        (await Log(l => l.ResendAsync(stored.Id))).ErrorType.ShouldBe(ErrorType.Conflict);
    }

    [Fact]
    public async Task Tenants_can_override_wording_with_known_variables_only()
    {
        var userId = await CreateUserAsync("hal@example.com", "Hal");
        (await Templates(t => t.SaveOverrideAsync(NotificationTemplates.Identity.PasswordChanged, TemplateParts.EmailSubject, "Security notice for {{RecipientName}}"))).IsSuccess.ShouldBeTrue();
        (await Templates(t => t.SaveOverrideAsync(NotificationTemplates.Identity.PasswordChanged, TemplateParts.EmailSubject, "Hi {{Secret}}"))).Errors.ShouldHaveSingleItem().Message.ShouldContain("Secret");

        await Notify(new Notification(NotificationTemplates.Identity.PasswordChanged, NotificationRecipient.ToUser(userId), Channels: [NotificationChannel.Email]));
        await DeliverAsync();
        _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem().Subject.ShouldBe("Security notice for Hal");

        // Another tenant still gets the default wording.
        var other = await _app.InScopeAsync(null, async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var tenant = new Tenant("wording", "Wording");
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            return tenant.Id;
        });
        var view = await _app.InScopeAsync(other, sp => sp.GetRequiredService<INotificationTemplateAdmin>().GetAsync(NotificationTemplates.Identity.PasswordChanged));
        view.GetDataOrThrow().Parts.Single(p => p.Part == TemplateParts.EmailSubject).OverrideContent.ShouldBeNull();

        (await Templates(t => t.ResetOverrideAsync(NotificationTemplates.Identity.PasswordChanged, TemplateParts.EmailSubject))).IsSuccess.ShouldBeTrue();
        (await Templates(t => t.GetAsync(NotificationTemplates.Identity.PasswordChanged))).GetDataOrThrow()
            .Parts.Single(p => p.Part == TemplateParts.EmailSubject).OverrideContent.ShouldBeNull();
    }

    [Fact]
    public async Task Resend_queues_an_exact_copy()
    {
        var userId = await CreateUserAsync("ivy@example.com", "Ivy");
        await Notify(new Notification(NotificationTemplates.Identity.PasswordChanged, NotificationRecipient.ToUser(userId), Channels: [NotificationChannel.Email]));
        await DeliverAsync();
        var original = (await Messages(userId)).Single();

        (await Log(l => l.ResendAsync(original.Id))).IsSuccess.ShouldBeTrue();
        await DeliverAsync();

        var sent = _app.Sent.On(NotificationChannel.Email).ToList();
        sent.Count.ShouldBe(2);
        sent[1].HtmlBody.ShouldBe(sent[0].HtmlBody);
        (await Log(l => l.SearchAsync(new NotificationLogQuery(Search: "ivy@")))).Total.ShouldBe(2);
    }

    [Fact]
    public async Task A_misconfigured_channel_stops_startup_with_a_clear_message()
    {
        var registry = _app.Services.GetRequiredService<NotificationProviderRegistry>();
        var options = Options.Create(new NotificationOptions());
        options.Value.Channels[NotificationChannel.Sms] = new NotificationOptions.ChannelOptions { Enabled = true, Provider = "twilio-typo" };

        var error = await Should.ThrowAsync<InvalidOperationException>(() => new NotificationConfigurationCheck(registry, options).ExecuteAsync(CancellationToken.None));

        error.Message.ShouldContain("twilio-typo");
        error.Message.ShouldContain("Available: capture, log");
    }

    [Fact]
    public async Task Retention_removes_old_messages()
    {
        var userId = await CreateUserAsync("jo@example.com", "Jo");
        await Notify(new Notification(NotificationTemplates.Identity.PasswordChanged, NotificationRecipient.ToUser(userId)));
        _app.Time.Advance(TimeSpan.FromDays(366));

        var removed = await _app.InScopeAsync(null, sp => sp.GetServices<Base.Application.Operations.IScheduledJob>().OfType<Base.Infrastructure.Jobs.NotificationRetentionJob>().Single().PurgeAsync(CancellationToken.None));

        removed.ShouldBe(2);
        (await Messages(userId)).ShouldBeEmpty();
    }

    private static Notification Reminder(Guid userId) =>
        new(NotificationTemplates.General.Reminder, NotificationRecipient.ToUser(userId), new { Title = "Pay rent", Message = "Due on Friday." });

    private Task<Guid> CreateUserAsync(string email, string name, string? phone = null) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => CreateUserAsync(sp, email, name, phone));

    private static async Task<Guid> CreateUserAsync(IServiceProvider sp, string email, string name, string? phone)
    {
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = name, PhoneNumber = phone };
        (await users.CreateAsync(user, "Correct-Horse-Battery-Staple")).Succeeded.ShouldBeTrue();
        return user.Id;
    }

    private Task<ValidationResponse<IReadOnlyList<Guid>>> Notify(Notification notification) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<INotificationService>().SendAsync(notification));

    private Task DeliverAsync() =>
        ActivatorUtilities.CreateInstance<OutboxProcessor>(_app.Services).ProcessBatchAsync(_app.Services.GetRequiredService<IOptions<OutboxOptions>>().Value, CancellationToken.None);

    private Task<List<NotificationMessage>> Messages(Guid userId) =>
        _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<AppDbContext>().NotificationMessages.AsNoTracking()
            .Where(m => m.RecipientUserId == userId).OrderBy(m => m.DateCreated).ThenBy(m => m.Id).ToListAsync());

    private Task<T> Inbox<T>(Func<INotificationInbox, Task<T>> work) => _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<INotificationInbox>()));

    private Task<T> Preferences<T>(Func<INotificationPreferences, Task<T>> work) => _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<INotificationPreferences>()));

    private Task<T> Log<T>(Func<INotificationLog, Task<T>> work) => _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<INotificationLog>()));

    private Task<T> Templates<T>(Func<INotificationTemplateAdmin, Task<T>> work) => _app.InScopeAsync(_app.DefaultTenantId, sp => work(sp.GetRequiredService<INotificationTemplateAdmin>()));
}
