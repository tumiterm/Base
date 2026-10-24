using Base.Application.Common.Results;
using Base.Application.Notifications;
using Base.Application.Subscriptions;
using Base.Application.Tenancy;
using Base.Domain.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace Base.IntegrationTests.Notifications;

/// <summary>Rewording templates: whole saves as versions, the layout around them, and who may reword what.</summary>
public sealed partial class NotificationTests
{
    private static readonly string Changed = NotificationTemplates.Identity.PasswordChanged;

    [Fact]
    public async Task A_rewording_is_saved_as_a_version_cleaned_previewed_and_can_be_undone()
    {
        var userId = await CreateUserAsync("joy@example.com", "Joy");
        var saved = await Templates(t => t.SaveAsync(Changed, new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [TemplateParts.EmailSubject] = "Security notice for {{RecipientName}}",
            [TemplateParts.EmailHeading] = "Was this you?",
            [TemplateParts.EmailBody] = "<p>Hi {{RecipientName}}, your password changed.</p><script>alert(1)</script>",
            [TemplateParts.EmailButton] = "No, secure my account",
        }, "Shorter"));
        saved.Message.ShouldBe("Saved as version 1.");

        await Notify(new Notification(Changed, NotificationRecipient.ToUser(userId), Channels: [NotificationChannel.Email]));
        await DeliverAsync();
        var email = _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem();
        email.ShouldSatisfyAllConditions(
            e => e.Subject.ShouldBe("Security notice for Joy"),
            e => e.HtmlBody!.ShouldContain("Was this you?"),
            e => e.HtmlBody!.ShouldContain("Hi Joy, your password changed."),
            e => e.HtmlBody!.ShouldNotContain("<script"),
            e => e.HtmlBody!.ShouldContain("No, secure my account"),
            e => e.HtmlBody!.ShouldContain("https://localhost:7262/account/forgot-password", Case.Sensitive, "the button keeps the application's address"),
            e => e.Body!.ShouldContain("No, secure my account:\nhttps://localhost:7262/account/forgot-password"),
            e => e.Body!.ShouldNotContain("<p>"));

        // Only the words change: a part the application owns can't be reworded, nor can a placeholder be invented.
        (await Templates(t => t.SaveAsync(Changed, new Dictionary<string, string?>(StringComparer.Ordinal) { [TemplateParts.EmailAction] = "https://evil.example" })))
            .Errors.ShouldHaveSingleItem().Field.ShouldBe(TemplateParts.EmailAction);
        (await Templates(t => t.SaveAsync(Changed, new Dictionary<string, string?>(StringComparer.Ordinal) { [TemplateParts.EmailHeading] = "Hi {{Secret}}" })))
            .Errors.ShouldHaveSingleItem().Message.ShouldContain("Secret");

        // A draft can be previewed (with sample values) without saving it.
        var preview = (await Templates(t => t.PreviewAsync(Changed, new Dictionary<string, string?>(StringComparer.Ordinal) { [TemplateParts.EmailHeading] = "Draft heading" }))).GetDataOrThrow();
        preview.EmailHtml!.ShouldSatisfyAllConditions(h => h.ShouldContain("Draft heading"), h => h.ShouldContain("Hi "));
        (await Templates(t => t.GetAsync(Changed))).GetDataOrThrow().Part(TemplateParts.EmailHeading)!.Effective.ShouldBe("Was this you?");

        (await Templates(t => t.SaveAsync(Changed, new Dictionary<string, string?>(StringComparer.Ordinal) { [TemplateParts.EmailHeading] = "" }))).Message.ShouldBe("Saved as version 2.");
        (await Templates(t => t.SaveAsync(Changed, new Dictionary<string, string?>(StringComparer.Ordinal) { [TemplateParts.EmailHeading] = "" }))).Message.ShouldBe("Nothing changed.");
        (await Templates(t => t.ResetAsync(Changed))).IsSuccess.ShouldBeTrue();
        var view = (await Templates(t => t.GetAsync(Changed))).GetDataOrThrow();
        view.ShouldSatisfyAllConditions(
            v => v.IsCustomised.ShouldBeFalse(),
            v => v.Revisions!.Select(r => r.Number).ShouldBe([3, 2, 1]),
            v => v.Revisions![0].ChangedParts.ShouldBe(0));

        (await Templates(t => t.RestoreAsync(Changed, view.Revisions!.Single(r => r.Number == 1).Id))).Message.ShouldBe("Version 1 is back.");
        (await Templates(t => t.GetAsync(Changed))).GetDataOrThrow().ShouldSatisfyAllConditions(
            v => v.Part(TemplateParts.EmailHeading)!.Effective.ShouldBe("Was this you?"),
            v => v.Revisions!.Count.ShouldBe(4));

        // A test goes to the person editing, with sample values.
        _actor.UserId = userId;
        _app.Sent.Clear();
        (await Templates(t => t.SendTestAsync(Changed))).IsSuccess.ShouldBeTrue();
        _app.Sent.On(NotificationChannel.Email).ShouldHaveSingleItem().ShouldSatisfyAllConditions(m => m.To.ShouldBe("joy@example.com"), m => m.Subject!.ShouldBe("Test: Security notice for Joy"));
    }

    [Fact]
    public async Task Sign_in_links_keep_their_wording_and_organisations_reword_their_own_messages_only_on_plans_that_include_it()
    {
        (await Templates(t => t.SaveAsync(NotificationTemplates.Identity.PasswordReset, new Dictionary<string, string?>(StringComparer.Ordinal) { [TemplateParts.EmailHeading] = "Click quickly" })))
            .ErrorType.ShouldBe(ErrorType.Conflict);
        (await Templates(t => t.GetAsync(NotificationTemplates.Identity.PasswordReset))).GetDataOrThrow().Editing.ShouldBe(TemplateEditing.Locked);

        // The platform words what it sends organisations; they see neither those templates nor can they change them.
        (await Templates(t => t.SaveAsync(NotificationTemplates.Subscriptions.TrialEnding, new Dictionary<string, string?>(StringComparer.Ordinal) { [TemplateParts.EmailSubject] = "Last days of your trial" })))
            .IsSuccess.ShouldBeTrue();
        var basic = await PlanAsync("basic", wording: false);
        var premium = await PlanAsync("premium", wording: true);
        var plain = await TenantAsync("plain", basic);
        var rich = await TenantAsync("rich", premium);

        var listed = await _app.InScopeAsync(plain, sp => sp.GetRequiredService<INotificationTemplateAdmin>().ListAsync());
        listed.ShouldNotContain(t => t.Definition.Sender == NotificationSender.Platform);
        listed.Single(t => t.Definition.Code == Changed).Editing.ShouldBe(TemplateEditing.NotInPlan);
        (await _app.InScopeAsync(plain, sp => sp.GetRequiredService<INotificationTemplateAdmin>().SaveAsync(Changed, Subject("Plain notice")))).ErrorType.ShouldBe(ErrorType.Forbidden);
        (await _app.InScopeAsync(plain, sp => sp.GetRequiredService<INotificationTemplateAdmin>().GetAsync(NotificationTemplates.Subscriptions.TrialEnding))).ErrorType.ShouldBe(ErrorType.NotFound);

        (await _app.InScopeAsync(rich, sp => sp.GetRequiredService<INotificationTemplateAdmin>().SaveAsync(Changed, Subject("Rich notice")))).IsSuccess.ShouldBeTrue();

        _app.Sent.Clear();
        foreach (var tenant in new[] { plain, rich })
        {
            await _app.InScopeAsync(tenant, async sp =>
            {
                var notifications = sp.GetRequiredService<INotificationService>();
                await notifications.SendAsync(new Notification(Changed, NotificationRecipient.ToEmail($"x@{tenant:N}.test"), Channels: [NotificationChannel.Email]));
                return await notifications.SendAsync(new Notification(
                    NotificationTemplates.Subscriptions.TrialEnding, NotificationRecipient.ToEmail($"y@{tenant:N}.test"), new { PlanName = "Basic", EndsOn = "1 May" }));
            });
        }

        await DeliverAsync();
        var sent = _app.Sent.On(NotificationChannel.Email);
        sent.Single(m => m.To == $"x@{plain:N}.test").Subject.ShouldBe("Your Base password was changed", "its plan doesn't include rewording");
        sent.Single(m => m.To == $"x@{rich:N}.test").Subject.ShouldBe("Rich notice");
        sent.Where(m => m.To.StartsWith("y@", StringComparison.Ordinal)).Select(m => m.Subject).ShouldAllBe(s => s == "Last days of your trial");
    }

    private static Dictionary<string, string?> Subject(string subject) => new(StringComparer.Ordinal) { [TemplateParts.EmailSubject] = subject };

    private async Task<Guid> PlanAsync(string code, bool wording) =>
        (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<IPlanAdministration>().CreateAsync(
            new SavePlanRequest(code, code, null, "ZAR", 100m, 1000m, 0, 0,
                new Dictionary<string, long?>(StringComparer.Ordinal) { [FeatureCodes.CustomEmailWording] = wording ? 1 : 0 })))).GetDataOrThrow();

    private async Task<Guid> TenantAsync(string code, Guid planId) =>
        (await _app.InScopeAsync(_app.DefaultTenantId, sp => sp.GetRequiredService<ITenantAdministration>().CreateAsync(
            new CreateTenantRequest(code, code, null, $"admin@{code}.test", "Admin", planId, WithTrial: false)))).GetDataOrThrow();
}
