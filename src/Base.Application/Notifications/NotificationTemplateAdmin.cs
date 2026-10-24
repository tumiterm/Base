using System.Text.Json;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Common.Text;
using Base.Application.Communications;
using Base.Application.Security;
using Base.Application.Settings;
using Base.Application.Subscriptions;
using Base.Domain.Notifications;

namespace Base.Application.Notifications;

/// <summary>Whether, and why not, the wording of a template can be changed here.</summary>
public enum TemplateEditing
{
    Editable = 0,

    /// <summary>The organisation's plan doesn't include custom email wording.</summary>
    NotInPlan = 1,

    /// <summary>A security message with a sign-in link: its wording stays the application's.</summary>
    Locked = 2,

    /// <summary>Sent by the platform: only the platform words it.</summary>
    ByPlatform = 3,
}

public sealed record TemplatePartView(string Part, string DefaultContent, string? OverrideContent, bool IsEditable)
{
    public string Effective => OverrideContent ?? DefaultContent;
}

public sealed record TemplateRevisionView(Guid Id, int Number, DateTimeOffset SavedAt, string? SavedBy, string? Note, int ChangedParts);

public sealed record NotificationTemplateView(
    NotificationTemplateDefinition Definition,
    IReadOnlyList<TemplatePartView> Parts,
    TemplateEditing Editing = TemplateEditing.Editable,
    IReadOnlyList<TemplateRevisionView>? Revisions = null)
{
    public bool IsCustomised => Parts.Any(p => p.OverrideContent is not null);

    public bool CanEdit => Editing == TemplateEditing.Editable;

    /// <summary>The placeholders the wording may use.</summary>
    public IReadOnlyList<string> Variables => [.. Definition.Variables.Concat(NotificationCatalog.BuiltInVariables).Order(StringComparer.Ordinal)];

    public TemplatePartView? Part(string part) => Parts.FirstOrDefault(p => string.Equals(p.Part, part, StringComparison.Ordinal));
}

/// <summary>A template filled with sample values, as each of its channels would show it.</summary>
public sealed record TemplatePreview(string? Subject, string? EmailHtml, string? EmailText, string? Sms, string? InAppTitle, string? InAppBody);

/// <summary>
/// The wording of what the application sends, and this tenant's rewording of it: saved as a whole with a version
/// history, previewed with sample values and sent as a test before anyone else gets it. An organisation rewords its
/// own messages when its plan includes custom email wording; the platform words what it sends organisations; nobody
/// rewords a security message carrying a sign-in link. Changing needs <c>Notifications.Manage</c>.
/// </summary>
public interface INotificationTemplateAdmin
{
    /// <summary>The templates this tenant sends (an organisation doesn't see the platform's own).</summary>
    Task<IReadOnlyList<NotificationTemplateView>> ListAsync(CancellationToken cancellationToken = default);

    Task<ValidationResponse<NotificationTemplateView>> GetAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the wording of every part given (a part left empty or as the default goes back to the default) as one new
    /// version. Only the template's placeholders may be used; the body is cleaned to simple HTML.
    /// </summary>
    Task<ValidationResponse> SaveAsync(string code, IReadOnlyDictionary<string, string?> parts, string? note = null, CancellationToken cancellationToken = default);

    /// <summary>Rewords one part (the other parts stay as they are).</summary>
    Task<ValidationResponse> SaveOverrideAsync(string code, string part, string content, CancellationToken cancellationToken = default);

    Task<ValidationResponse> ResetOverrideAsync(string code, string part, CancellationToken cancellationToken = default);

    /// <summary>Back to the application's wording, as a new version (the earlier ones stay to go back to).</summary>
    Task<ValidationResponse> ResetAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Brings back an earlier version's wording, as a new version.</summary>
    Task<ValidationResponse> RestoreAsync(string code, Guid revisionId, CancellationToken cancellationToken = default);

    /// <summary>The template with sample values: as saved, or with <paramref name="draft"/> parts not saved yet.</summary>
    Task<ValidationResponse<TemplatePreview>> PreviewAsync(string code, IReadOnlyDictionary<string, string?>? draft = null, CancellationToken cancellationToken = default);

    /// <summary>Emails the preview to the signed-in person, straight through the email provider.</summary>
    Task<ValidationResponse> SendTestAsync(string code, IReadOnlyDictionary<string, string?>? draft = null, CancellationToken cancellationToken = default);
}

internal sealed class NotificationTemplateAdmin(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IPlatformTenant platform,
    ITenantEntitlements entitlements,
    IRichText richText,
    NotificationComposer composer,
    INotificationChannels channels,
    INotificationRecipientResolver recipients,
    ITenantAddress address,
    IApplicationSettingService settings) : INotificationTemplateAdmin
{
    public async Task<IReadOnlyList<NotificationTemplateView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var isPlatform = await platform.IsCurrentAsync(cancellationToken);
        var wording = await entitlements.HasFeatureAsync(FeatureCodes.CustomEmailWording, cancellationToken);
        var overrides = await unitOfWork.Repository<NotificationTemplateOverride>().GetAllAsync(
            o => new { o.TemplateCode, o.Part, o.Content }, cancellationToken: cancellationToken);
        return NotificationCatalog.All
            .Where(t => isPlatform || t.Sender == NotificationSender.Organisation)
            .Select(t => View(
                t,
                overrides.Where(o => string.Equals(o.TemplateCode, t.Code, StringComparison.Ordinal)).ToDictionary(o => o.Part, o => o.Content, StringComparer.Ordinal),
                Editing(t, isPlatform, wording)))
            .ToList();
    }

    public async Task<ValidationResponse<NotificationTemplateView>> GetAsync(string code, CancellationToken cancellationToken = default)
    {
        var view = (await ListAsync(cancellationToken)).FirstOrDefault(v => string.Equals(v.Definition.Code, code, StringComparison.Ordinal));
        if (view is null)
        {
            return ValidationResponse.NotFound("Template not found.").As<NotificationTemplateView>();
        }

        var revisions = await unitOfWork.Repository<NotificationTemplateRevision>().GetAllAsync(
            r => new { r.Id, r.Number, r.DateCreated, r.UserCreated, r.Note, r.PartsJson }, r => r.TemplateCode == code, cancellationToken: cancellationToken);
        return view with
        {
            Revisions = revisions.OrderByDescending(r => r.Number)
                .Select(r => new TemplateRevisionView(r.Id, r.Number, r.DateCreated, r.UserCreated, r.Note, Read(r.PartsJson).Count))
                .ToList(),
        };
    }

    public async Task<ValidationResponse> SaveAsync(string code, IReadOnlyDictionary<string, string?> parts, string? note = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var (template, problem) = await EditableAsync(code, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var cleaned = Clean(template!, parts, out var errors);
        if (errors.Count > 0)
        {
            return ValidationResponse.Failure(errors);
        }

        var current = await OverridesAsync(code, cancellationToken);
        var wanted = new Dictionary<string, string>(current, StringComparer.Ordinal);
        foreach (var (part, content) in cleaned)
        {
            if (content is null)
            {
                wanted.Remove(part);
            }
            else
            {
                wanted[part] = content;
            }
        }

        return await ApplyAsync(code, current, wanted, note, cancellationToken);
    }

    public Task<ValidationResponse> SaveOverrideAsync(string code, string part, string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult(ValidationResponse.Failure(new ValidationError("content", $"Enter between 1 and {NotificationTemplateOverride.ContentMaxLength} characters.")));
        }

        return SaveAsync(code, new Dictionary<string, string?>(StringComparer.Ordinal) { [part] = content }, null, cancellationToken);
    }

    public async Task<ValidationResponse> ResetOverrideAsync(string code, string part, CancellationToken cancellationToken = default)
    {
        var (template, problem) = await EditableAsync(code, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (!EditablePartsOf(template!).Contains(part, StringComparer.Ordinal))
        {
            return ValidationResponse.NotFound("Template part not found.");
        }

        var current = await OverridesAsync(code, cancellationToken);
        var wanted = new Dictionary<string, string>(current, StringComparer.Ordinal);
        wanted.Remove(part);
        var result = await ApplyAsync(code, current, wanted, null, cancellationToken);
        return result.IsSuccess ? ValidationResponse.Success("Default restored.") : result;
    }

    public async Task<ValidationResponse> ResetAsync(string code, CancellationToken cancellationToken = default)
    {
        var (_, problem) = await EditableAsync(code, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var current = await OverridesAsync(code, cancellationToken);
        var result = await ApplyAsync(code, current, new Dictionary<string, string>(StringComparer.Ordinal), "Back to the application's wording", cancellationToken);
        return result.IsSuccess ? ValidationResponse.Success("The application's wording is back.") : result;
    }

    public async Task<ValidationResponse> RestoreAsync(string code, Guid revisionId, CancellationToken cancellationToken = default)
    {
        var (template, problem) = await EditableAsync(code, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var revision = await unitOfWork.Repository<NotificationTemplateRevision>().GetAsync(r => r.Id == revisionId && r.TemplateCode == code, cancellationToken: cancellationToken);
        if (revision is null)
        {
            return ValidationResponse.NotFound("That version doesn't exist.");
        }

        // Saved with an earlier release, a version may name a part or placeholder that no longer exists.
        var wanted = Clean(template!, Read(revision.PartsJson).ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal), out var errors);
        if (errors.Count > 0)
        {
            return ValidationResponse.Failure($"Version {revision.Number} can't come back as it was: {string.Join(" ", errors.Select(e => e.Message))}");
        }

        var current = await OverridesAsync(code, cancellationToken);
        var result = await ApplyAsync(
            code, current, wanted.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!, StringComparer.Ordinal), $"Version {revision.Number} brought back", cancellationToken);
        return result.IsSuccess ? ValidationResponse.Success($"Version {revision.Number} is back.") : result;
    }

    public async Task<ValidationResponse<TemplatePreview>> PreviewAsync(string code, IReadOnlyDictionary<string, string?>? draft = null, CancellationToken cancellationToken = default)
    {
        var template = NotificationCatalog.Find(code);
        if (template is null || (template.Sender == NotificationSender.Platform && !await platform.IsCurrentAsync(cancellationToken)))
        {
            return ValidationResponse.NotFound("Template not found.").As<TemplatePreview>();
        }

        // What would be sent: the saved rewording only where it applies, then the draft on top.
        var editing = Editing(template, await platform.IsCurrentAsync(cancellationToken), await entitlements.HasFeatureAsync(FeatureCodes.CustomEmailWording, cancellationToken));
        var defaults = NotificationTemplateFiles.PartsOf(code);
        var parts = new Dictionary<string, string>(defaults, StringComparer.Ordinal);
        if (editing == TemplateEditing.Editable)
        {
            foreach (var (part, content) in await OverridesAsync(code, cancellationToken))
            {
                parts[part] = content;
            }
        }

        if (draft is not null)
        {
            var cleaned = Clean(template, draft, out var errors);
            if (errors.Count > 0)
            {
                return ValidationResponse.Failure(errors).As<TemplatePreview>();
            }

            foreach (var (part, content) in cleaned)
            {
                parts[part] = content ?? defaults.GetValueOrDefault(part) ?? string.Empty;
            }
        }

        var values = await SampleValuesAsync(template, cancellationToken);
        string? Plain(string part) => parts.TryGetValue(part, out var p) && p.Length > 0 ? TemplateRenderer.Render(p, values, html: false) : null;
        var email = parts.ContainsKey(TemplateParts.EmailBody) && parts.ContainsKey(TemplateParts.EmailSubject) ? composer.RenderEmail(parts, values) : null;
        return ValidationResponse.Success(new TemplatePreview(email?.Subject, email?.HtmlBody, email?.Body, Plain(TemplateParts.Sms), Plain(TemplateParts.InAppTitle), Plain(TemplateParts.InAppBody)));
    }

    public async Task<ValidationResponse> SendTestAsync(string code, IReadOnlyDictionary<string, string?>? draft = null, CancellationToken cancellationToken = default)
    {
        if (!currentUser.HasPermission(PermissionCodes.Notifications.Manage) || currentUser.UserId is not { } userId)
        {
            return ValidationResponse.Forbidden();
        }

        var preview = await PreviewAsync(code, draft, cancellationToken);
        if (preview.IsError)
        {
            return preview;
        }

        if (preview.Data!.EmailHtml is null)
        {
            return ValidationResponse.Failure("This template isn't sent by email.");
        }

        var me = await recipients.ResolveAsync(NotificationRecipient.ToUser(userId), cancellationToken);
        if (me?.Email is not { Length: > 0 } to)
        {
            return ValidationResponse.Failure("Your account has no email address to send the test to.");
        }

        var provider = await channels.ResolveAsync(NotificationChannel.Email, cancellationToken);
        if (provider is null)
        {
            return ValidationResponse.Failure($"Email can't be sent: {(await channels.DescribeAsync(NotificationChannel.Email, cancellationToken)).Description}");
        }

        var result = await provider.SendAsync(
            new OutgoingMessage(Guid.NewGuid(), NotificationChannel.Email, to, me.Name, "Test: " + preview.Data.Subject, preview.Data.EmailText ?? string.Empty, preview.Data.EmailHtml, null, []),
            cancellationToken);
        return result.Succeeded
            ? ValidationResponse.Success($"Test sent to {to}, with sample values.")
            : ValidationResponse.Failure($"The test wasn't sent. The server said: {result.Error}");
    }

    /// <summary>The parts this template lets people reword, for the channels it uses.</summary>
    public static IReadOnlyList<string> EditablePartsOf(NotificationTemplateDefinition template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var used = template.Channels.Concat(template.Fallback ?? []).ToHashSet();
        var defaults = NotificationTemplateFiles.PartsOf(template.Code);
        var parts = new List<string>();
        if (used.Contains(NotificationChannel.Email))
        {
            parts.AddRange([TemplateParts.EmailSubject, TemplateParts.EmailPreheader, TemplateParts.EmailHeading, TemplateParts.EmailBody]);
            if (defaults.ContainsKey(TemplateParts.EmailAction))
            {
                parts.Add(TemplateParts.EmailButton);
            }
        }

        if (used.Contains(NotificationChannel.Sms))
        {
            parts.Add(TemplateParts.Sms);
        }

        if (used.Contains(NotificationChannel.InApp))
        {
            parts.AddRange([TemplateParts.InAppTitle, TemplateParts.InAppBody]);
        }

        return parts;
    }

    private static TemplateEditing Editing(NotificationTemplateDefinition template, bool isPlatform, bool hasWording) =>
        !template.IsRewordable ? TemplateEditing.Locked
        : template.Sender == NotificationSender.Platform && !isPlatform ? TemplateEditing.ByPlatform
        : !hasWording ? TemplateEditing.NotInPlan
        : TemplateEditing.Editable;

    private async Task<(NotificationTemplateDefinition? Template, ValidationResponse? Problem)> EditableAsync(string code, CancellationToken cancellationToken)
    {
        if (!currentUser.HasPermission(PermissionCodes.Notifications.Manage))
        {
            return (null, ValidationResponse.Forbidden());
        }

        var template = NotificationCatalog.Find(code);
        if (template is null)
        {
            return (null, ValidationResponse.NotFound("Template part not found."));
        }

        var editing = Editing(template, await platform.IsCurrentAsync(cancellationToken), await entitlements.HasFeatureAsync(FeatureCodes.CustomEmailWording, cancellationToken));
        return editing switch
        {
            TemplateEditing.Editable => (template, null),
            TemplateEditing.Locked => (null, ValidationResponse.Conflict($"{template.Name} carries a sign-in link, so it keeps the application's wording.")),
            TemplateEditing.ByPlatform => (null, ValidationResponse.NotFound("Template not found.")),
            _ => (null, ValidationResponse.Forbidden("Your plan doesn't include custom email wording.")),
        };
    }

    /// <summary>The given parts cleaned and checked; a part that is empty or the default becomes null (use the default).</summary>
    private Dictionary<string, string?> Clean(NotificationTemplateDefinition template, IReadOnlyDictionary<string, string?> parts, out List<ValidationError> errors)
    {
        errors = [];
        var editable = EditablePartsOf(template);
        var defaults = NotificationTemplateFiles.PartsOf(template.Code);
        var allowed = template.Variables.Concat(NotificationCatalog.BuiltInVariables).ToHashSet(StringComparer.Ordinal);
        var cleaned = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (part, raw) in parts)
        {
            if (!editable.Contains(part, StringComparer.Ordinal))
            {
                errors.Add(new ValidationError(part, $"'{part}' can't be reworded in {template.Name}."));
                continue;
            }

            var content = (raw ?? string.Empty).Trim();
            if (TemplateParts.IsHtml(part) && content.Length > 0)
            {
                content = richText.Sanitize(content);
            }

            if (content.Length > NotificationTemplateOverride.ContentMaxLength)
            {
                errors.Add(new ValidationError(part, $"Keep it under {NotificationTemplateOverride.ContentMaxLength} characters."));
                continue;
            }

            var unknown = TemplateRenderer.VariablesIn(content).Where(v => !allowed.Contains(v)).ToList();
            if (unknown.Count > 0)
            {
                errors.Add(new ValidationError(part,
                    $"Unknown variable(s): {string.Join(", ", unknown)}. Available: {string.Join(", ", allowed.Order(StringComparer.Ordinal))}."));
                continue;
            }

            cleaned[part] = content.Length == 0 || string.Equals(content, defaults.GetValueOrDefault(part)?.Trim(), StringComparison.Ordinal) ? null : content;
        }

        return cleaned;
    }

    private async Task<Dictionary<string, string>> OverridesAsync(string code, CancellationToken cancellationToken) =>
        (await unitOfWork.Repository<NotificationTemplateOverride>().GetAllAsync(o => new { o.Part, o.Content }, o => o.TemplateCode == code, cancellationToken: cancellationToken))
            .Where(o => TemplateParts.IsEditable(o.Part))
            .ToDictionary(o => o.Part, o => o.Content, StringComparer.Ordinal);

    /// <summary>Makes the rewording <paramref name="wanted"/> and records it as a new version, if anything changed.</summary>
    private async Task<ValidationResponse> ApplyAsync(string code, Dictionary<string, string> current, Dictionary<string, string> wanted, string? note, CancellationToken cancellationToken)
    {
        if (current.Count == wanted.Count && current.All(c => wanted.TryGetValue(c.Key, out var w) && string.Equals(w, c.Value, StringComparison.Ordinal)))
        {
            return ValidationResponse.Success("Nothing changed.");
        }

        var repository = unitOfWork.Repository<NotificationTemplateOverride>();
        foreach (var row in await repository.GetAllAsync(filter: o => o.TemplateCode == code, cancellationToken: cancellationToken))
        {
            if (!wanted.TryGetValue(row.Part, out var content))
            {
                repository.HardDelete(row); // an override is configuration, not business data: the versions keep its history
            }
            else if (!string.Equals(row.Content, content, StringComparison.Ordinal))
            {
                row.Change(content);
            }
        }

        foreach (var (part, content) in wanted.Where(w => !current.ContainsKey(w.Key)))
        {
            await repository.AddAsync(new NotificationTemplateOverride(code, part, content), cancellationToken);
        }

        var revisions = unitOfWork.Repository<NotificationTemplateRevision>();
        var number = await revisions.CountAsync(r => r.TemplateCode == code, cancellationToken) + 1;
        await revisions.AddAsync(new NotificationTemplateRevision(code, number, JsonSerializer.Serialize(wanted, NotificationComposer.Json), note), cancellationToken);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success($"Saved as version {number}.");
    }

    private async Task<Dictionary<string, string?>> SampleValuesAsync(NotificationTemplateDefinition template, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string?>(NotificationSamples.For(template), StringComparer.Ordinal);
        values[NotificationCatalog.AppNameVariable] = await settings.GetAsync(SettingCodes.General.ApplicationName, cancellationToken) ?? "Base";
        values[NotificationCatalog.PublicBaseUrlVariable] = await address.BaseUrlAsync(cancellationToken);
        var me = currentUser.UserId is { } id ? await recipients.ResolveAsync(NotificationRecipient.ToUser(id), cancellationToken) : null;
        values[NotificationCatalog.RecipientNameVariable] = me?.Name is { Length: > 0 } name ? name : "Sam Sample";
        return values;
    }

    private static Dictionary<string, string> Read(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, NotificationComposer.Json) ?? new(StringComparer.Ordinal);

    private static NotificationTemplateView View(NotificationTemplateDefinition template, Dictionary<string, string> overrides, TemplateEditing editing)
    {
        var defaults = NotificationTemplateFiles.PartsOf(template.Code);
        var editable = EditablePartsOf(template);
        var order = TemplateParts.All.ToList();
        var parts = defaults.Keys.Union(editable, StringComparer.Ordinal)
            .OrderBy(order.IndexOf)
            .Select(p => new TemplatePartView(p, defaults.GetValueOrDefault(p) ?? string.Empty, overrides.GetValueOrDefault(p), editable.Contains(p, StringComparer.Ordinal)))
            .ToList();
        return new NotificationTemplateView(template, parts, editing);
    }
}
