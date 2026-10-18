using System.Text;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Communications;
using Base.Application.Notifications;
using Base.Application.Security;
using Base.Application.Settings;
using Base.Application.Subscriptions;
using Base.Domain.Communications;
using Base.Domain.Notifications;
using Base.Web.Results;
using Base.Web.Security;
using Base.Web.Subscriptions;
using Base.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers;

/// <summary>Writing to people: one at a time, or in bulk to an audience or a pasted list, now or later.</summary>
[Route("messages")]
[HasPermission(PermissionCodes.Messages.Send)]
[RequiresFeature(FeatureCodes.Messaging)]
public sealed class MessagesController(
    IMessagingService messaging,
    IMessageTemplateService templates,
    INotificationChannels channels,
    IApplicationSettingService settings) : Controller
{
    private const long MaxListBytes = 2 * 1024 * 1024;

    [HttpGet("")]
    public async Task<IActionResult> Index(BroadcastStatus? status, int page = 1, CancellationToken cancellationToken = default) =>
        View(new MessageListViewModel(await messaging.ListAsync(new BroadcastQuery(status, page, PageRequest.DefaultPageSize), cancellationToken), status));

    /// <summary>A new message, optionally to someone already chosen (<c>?audience=people&amp;keys=...</c>) or from a template.</summary>
    [HttpGet("new")]
    public async Task<IActionResult> New(string? audience, [FromQuery] IList<string>? keys, Guid? template, CancellationToken cancellationToken)
    {
        var form = new ComposeForm { AudienceCode = audience ?? AudienceCodes.People, Keys = keys ?? new List<string>() };
        if (template is { } templateId && (await templates.GetAsync(templateId, cancellationToken)).Data is { } chosen)
        {
            form.TemplateId = chosen.Id;
            form.Title = chosen.Name;
            form.Subject = chosen.Subject;
            form.HtmlBody = chosen.HtmlBody;
            form.SmsBody = chosen.SmsBody;
            form.WhatsAppTemplateName = chosen.WhatsAppTemplateName;
            form.WhatsAppLanguage = chosen.WhatsAppLanguage ?? "en";
            form.WhatsAppParameters = [.. chosen.WhatsAppParameters];
        }

        return View("Compose", await ComposeAsync(form, null, null, null, cancellationToken));
    }

    [HttpPost("new")]
    [RequestSizeLimit(MaxListBytes + (512 * 1024))]
    public async Task<IActionResult> New(ComposeForm form, CancellationToken cancellationToken) => await SubmitAsync(form, null, cancellationToken);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, NotificationStatus? status, int page = 1, CancellationToken cancellationToken = default)
    {
        var message = await messaging.GetAsync(id, cancellationToken);
        if (message.IsError)
        {
            return NotFound();
        }

        var details = message.Data!;
        var local = details.Summary.IsScheduled ? await LocalAsync(details.Summary.ScheduledFor, cancellationToken) : (DateTime?)null;
        return View(new MessageDetailsViewModel(details, await messaging.DeliveriesAsync(id, status, page, cancellationToken), status, local));
    }

    [HttpGet("{id:guid}/edit")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var message = await messaging.GetAsync(id, cancellationToken);
        if (message.IsError)
        {
            return NotFound();
        }

        if (message.Data!.Summary.Status != BroadcastStatus.Scheduled)
        {
            this.Flash(ValidationResponse.Failure("It has already gone out, so it can't be changed. Send it again as a new message instead."));
            return RedirectToAction(nameof(Details), new { id });
        }

        var draft = message.Data.Draft;
        var form = ComposeForm.From(draft, draft.SendAt is { } at ? await LocalAsync(at, cancellationToken) : null);
        return View("Compose", await ComposeAsync(form, null, null, id, cancellationToken));
    }

    [HttpPost("{id:guid}/edit")]
    [RequestSizeLimit(MaxListBytes + (512 * 1024))]
    public async Task<IActionResult> Edit(Guid id, ComposeForm form, CancellationToken cancellationToken) => await SubmitAsync(form, id, cancellationToken);

    /// <summary>A new message with the same wording and audience.</summary>
    [HttpGet("{id:guid}/again")]
    public async Task<IActionResult> Again(Guid id, CancellationToken cancellationToken)
    {
        var message = await messaging.GetAsync(id, cancellationToken);
        if (message.IsError)
        {
            return NotFound();
        }

        var form = ComposeForm.From(message.Data!.Draft with { SendAt = null }, null);
        return View("Compose", await ComposeAsync(form, null, null, null, cancellationToken));
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        this.Flash(await messaging.CancelAsync(id, cancellationToken));
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>People to pick, for the composer's search box.</summary>
    [HttpGet("audiences/{code}/search")]
    public async Task<IActionResult> Search(string code, string? term, CancellationToken cancellationToken)
    {
        var found = await messaging.SearchAsync(code, term ?? string.Empty, cancellationToken);
        return found.IsSuccess
            ? Json(found.Data!.Select(m => new { m.Key, m.Name, Detail = string.Join(" · ", new[] { m.Email, m.Phone }.Where(a => !string.IsNullOrWhiteSpace(a))) }))
            : found.ToActionResult(this);
    }

    /// <summary>How many the current audience choice reaches, for the composer's live summary.</summary>
    [HttpPost("reach")]
    [RequestSizeLimit(MaxListBytes + (512 * 1024))]
    public async Task<IActionResult> Reach(ComposeForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        await ReadListFileAsync(form, cancellationToken);
        var reach = await messaging.ReachAsync(form.Audience(), cancellationToken);
        return reach.IsSuccess
            ? Json(new
            {
                reach.Data!.Label,
                reach.Data.People,
                reach.Data.WithEmail,
                reach.Data.WithPhone,
                reach.Data.WithAccount,
                Fields = reach.Data.Fields.Select(f => new { f.Name, f.Label }),
                reach.Data.Problems,
            })
            : Json(new { Error = reach.Message ?? (reach.Errors.Count > 0 ? reach.Errors[0].Message : "That audience can't be used.") });
    }

    [HttpGet("templates")]
    public async Task<IActionResult> Templates(CancellationToken cancellationToken) => View(await templates.ListAsync(cancellationToken));

    [HttpGet("templates/new")]
    public IActionResult NewTemplate() => View("Template", new TemplateForm());

    [HttpPost("templates/new")]
    public async Task<IActionResult> NewTemplate(TemplateForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        var result = await templates.CreateAsync(form.ToRequest(), cancellationToken);
        if (result.IsSuccess)
        {
            this.Flash(result);
            return RedirectToAction(nameof(Templates));
        }

        result.AddToModelState(ModelState);
        return View("Template", form);
    }

    [HttpGet("templates/{id:guid}")]
    public async Task<IActionResult> Template(Guid id, CancellationToken cancellationToken)
    {
        var template = await templates.GetAsync(id, cancellationToken);
        return template.IsSuccess ? View(TemplateForm.From(template.Data!)) : NotFound();
    }

    [HttpPost("templates/{id:guid}")]
    public async Task<IActionResult> Template(Guid id, TemplateForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        form.Id = id;
        var result = await templates.UpdateAsync(id, form.ToRequest(), cancellationToken);
        if (result.IsSuccess)
        {
            this.Flash(result);
            return RedirectToAction(nameof(Templates));
        }

        result.AddToModelState(ModelState);
        return View(form);
    }

    [HttpPost("templates/{id:guid}/delete")]
    public async Task<IActionResult> DeleteTemplate(Guid id, CancellationToken cancellationToken)
    {
        this.Flash(await templates.DeleteAsync(id, cancellationToken));
        return RedirectToAction(nameof(Templates));
    }

    private async Task<IActionResult> SubmitAsync(ComposeForm form, Guid? editing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        await ReadListFileAsync(form, cancellationToken);
        var draft = form.ToDraft(form.SendAtLocal is { } local ? await UtcAsync(local, cancellationToken) : null);
        if (string.Equals(form.When, "later", StringComparison.Ordinal) && form.SendAtLocal is null)
        {
            ModelState.AddModelError(nameof(ComposeForm.SendAtLocal), "Choose when to send it.");
            return View("Compose", await ComposeAsync(form, null, null, editing, cancellationToken));
        }

        switch (form.Action)
        {
            case "preview":
                {
                    var preview = await messaging.PreviewAsync(draft, cancellationToken);
                    preview.AddToModelState(ModelState);
                    if (preview.IsError && preview.Errors.Count == 0)
                    {
                        ModelState.AddModelError(string.Empty, preview.Message ?? "It can't be previewed.");
                    }

                    return View("Compose", await ComposeAsync(form, preview.Data, null, editing, cancellationToken));
                }

            case "test":
                {
                    var test = await messaging.SendTestAsync(draft, cancellationToken);
                    if (test.IsSuccess)
                    {
                        this.Flash(test);
                    }
                    else
                    {
                        test.AddToModelState(ModelState);
                        if (test.Errors.Count == 0)
                        {
                            ModelState.AddModelError(string.Empty, test.Message ?? "The test couldn't be sent.");
                        }
                    }

                    return View("Compose", await ComposeAsync(form, null, null, editing, cancellationToken));
                }
        }

        if (editing is { } id)
        {
            var updated = await messaging.UpdateAsync(id, draft, cancellationToken);
            if (updated.IsSuccess)
            {
                this.Flash(updated);
                return RedirectToAction(nameof(Details), new { id });
            }

            return await FailedAsync(form, updated, editing, cancellationToken);
        }

        var sent = await messaging.SendAsync(draft, cancellationToken);
        if (sent.IsSuccess)
        {
            this.Flash(sent);
            return RedirectToAction(nameof(Details), new { id = sent.Data });
        }

        return await FailedAsync(form, sent, null, cancellationToken);
    }

    private async Task<IActionResult> FailedAsync(ComposeForm form, ValidationResponse result, Guid? editing, CancellationToken cancellationToken)
    {
        result.AddToModelState(ModelState);
        if (result.Errors.Count == 0)
        {
            ModelState.AddModelError(string.Empty, result.Message ?? "It couldn't be sent.");
        }

        return View("Compose", await ComposeAsync(form, null, null, editing, cancellationToken));
    }

    private async Task<ComposeViewModel> ComposeAsync(ComposeForm form, MessagePreview? preview, AudienceReach? reach, Guid? editing, CancellationToken cancellationToken)
    {
        var audiences = await messaging.AudiencesAsync(cancellationToken);
        if (!audiences.Any(a => string.Equals(a.Code, form.AudienceCode, StringComparison.Ordinal)) && audiences.Count > 0)
        {
            form.AudienceCode = audiences[0].Code;
        }

        var chosen = string.Equals(form.Mode, ComposeForm.Pick, StringComparison.Ordinal)
            ? await messaging.ChosenAsync(form.AudienceCode, [.. form.Keys], cancellationToken)
            : [];

        var on = Enum.GetValues<NotificationChannel>().ToDictionary(c => c, channels.IsEnabled);
        var zone = await settings.GetAsync(SettingCodes.General.DisplayTimeZone, cancellationToken) ?? "UTC";
        return new ComposeViewModel(form, audiences, await templates.ListAsync(cancellationToken), on, chosen, reach, preview, zone, editing);
    }

    private async Task ReadListFileAsync(ComposeForm form, CancellationToken cancellationToken)
    {
        if (form.ListFile is not { Length: > 0 } file)
        {
            return;
        }

        if (file.Length > MaxListBytes)
        {
            ModelState.AddModelError(nameof(ComposeForm.ListFile), "The list is over 2 MB; split it.");
            return;
        }

        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        form.ListText = await reader.ReadToEndAsync(cancellationToken);
    }

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken)
    {
        var id = await settings.GetAsync(SettingCodes.General.DisplayTimeZone, cancellationToken);
        return id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;
    }

    private async Task<DateTimeOffset> UtcAsync(DateTime local, CancellationToken cancellationToken) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), await ZoneAsync(cancellationToken)), TimeSpan.Zero);

    private async Task<DateTime> LocalAsync(DateTimeOffset utc, CancellationToken cancellationToken) =>
        TimeZoneInfo.ConvertTime(utc, await ZoneAsync(cancellationToken)).DateTime;
}
