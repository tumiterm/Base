using System.Text.Json;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Text;
using Base.Application.Communications;
using Base.Application.Settings;
using Base.Application.Subscriptions;
using Base.Domain.Notifications;

namespace Base.Application.Notifications;

/// <summary>Shared by the service and the dispatcher: can a channel be used for this recipient, and what does it say.</summary>
internal sealed class NotificationComposer(
    IUnitOfWork unitOfWork,
    INotificationChannels channels,
    IApplicationSettingService settings,
    ITenantContext tenant,
    IPlatformTenant platform,
    ITenantEntitlements entitlements,
    ITenantAddress address,
    IRichText richText)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Null when the channel can be used; otherwise why not (recorded on a skipped message).</summary>
    public async Task<string?> UnavailableReasonAsync(
        NotificationTemplateDefinition template,
        IReadOnlyDictionary<string, string> parts,
        NotificationChannel channel,
        NotificationRecipientSnapshot recipient,
        CancellationToken cancellationToken)
    {
        if (!channels.IsEnabled(channel))
        {
            return $"The {channel} channel is switched off.";
        }

        if (string.IsNullOrWhiteSpace(recipient.AddressFor(channel)))
        {
            return $"The recipient has no {channel} address.";
        }

        var hasContent = channel == NotificationChannel.WhatsApp
            ? template.WhatsApp is not null
            : TemplateParts.RequiredFor(channel).All(parts.ContainsKey);
        if (!hasContent)
        {
            return $"Template {template.Code} has no {channel} content.";
        }

        if (!template.IsMandatory && recipient.UserId is { } userId
            && await unitOfWork.Repository<NotificationPreference>().ExistsAsync(
                p => p.UserId == userId && p.Category == template.Category && p.Channel == channel && !p.IsEnabled, cancellationToken))
        {
            return $"The recipient opted out of {template.Category} on {channel}.";
        }

        return null;
    }

    /// <summary>
    /// The template's default parts with the rewording that applies: the platform's for what the platform sends, the
    /// organisation's for its own messages while its plan includes custom email wording. Security messages with a
    /// sign-in link always keep the application's wording.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> PartsAsync(string templateCode, CancellationToken cancellationToken)
    {
        var template = NotificationCatalog.Get(templateCode);
        var parts = new Dictionary<string, string>(NotificationTemplateFiles.PartsOf(templateCode), StringComparer.Ordinal);
        if (await RewordingFromAsync(template, cancellationToken) is not { } owner)
        {
            return parts;
        }

        IReadOnlyList<(string Part, string Content)> overrides;
        using (tenant.BeginSystemScope())
        {
            overrides = (await unitOfWork.Repository<NotificationTemplateOverride>().GetAllAsync(
                    o => new { o.Part, o.Content }, o => o.TenantId == owner && o.TemplateCode == templateCode, cancellationToken: cancellationToken))
                .Select(o => (o.Part, o.Content))
                .ToList();
        }

        foreach (var (part, content) in overrides.Where(o => TemplateParts.IsEditable(o.Part)))
        {
            parts[part] = content;
        }

        return parts;
    }

    /// <summary>Whose rewording of this template applies here, if anyone's.</summary>
    private async Task<Guid?> RewordingFromAsync(NotificationTemplateDefinition template, CancellationToken cancellationToken)
    {
        if (!template.IsRewordable)
        {
            return null;
        }

        if (template.Sender == NotificationSender.Platform)
        {
            return await platform.GetIdAsync(cancellationToken);
        }

        return tenant.IsResolved && await entitlements.HasFeatureAsync(FeatureCodes.CustomEmailWording, cancellationToken) ? tenant.TenantId : null;
    }

    public async Task<Dictionary<string, string?>> ValuesAsync(NotificationTemplateDefinition template, object? data, NotificationRecipientSnapshot recipient, CancellationToken cancellationToken)
    {
        var values = TemplateValues.From(data);
        values[NotificationCatalog.AppNameVariable] = await settings.GetAsync(SettingCodes.General.ApplicationName, cancellationToken);
        values[NotificationCatalog.PublicBaseUrlVariable] = tenant.IsResolved
            ? await address.BaseUrlAsync(cancellationToken)
            : (await settings.GetAsync(SettingCodes.General.PublicBaseUrl, cancellationToken))?.TrimEnd('/');
        values.TryAdd(NotificationCatalog.RecipientNameVariable, string.IsNullOrWhiteSpace(recipient.Name) ? "there" : recipient.Name);

        var missing = template.Variables.Where(v => !values.ContainsKey(v)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"Notification {template.Code} needs: {string.Join(", ", missing)}.", nameof(data));
        }

        return values;
    }

    public RenderedContent Render(NotificationTemplateDefinition template, IReadOnlyDictionary<string, string> parts, NotificationChannel channel, IReadOnlyDictionary<string, string?> values)
    {
        string Part(string name) => TemplateRenderer.Render(parts[name], values, TemplateParts.IsHtml(name));

        return channel switch
        {
            NotificationChannel.Email => RenderEmail(parts, values),
            NotificationChannel.Sms => new(null, Part(TemplateParts.Sms), null, null),
            NotificationChannel.InApp => new(Part(TemplateParts.InAppTitle), Part(TemplateParts.InAppBody), null, null),
            NotificationChannel.WhatsApp => new(null, null, null,
                JsonSerializer.Serialize(template.WhatsApp!.Parameters.Select(p => values.GetValueOrDefault(p) ?? string.Empty).ToArray(), Json)),
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };
    }

    /// <summary>The email in its layout, and its text version (the template's own, or made from the parts).</summary>
    public RenderedContent RenderEmail(IReadOnlyDictionary<string, string> parts, IReadOnlyDictionary<string, string?> values)
    {
        string? Text(string name) =>
            parts.TryGetValue(name, out var part) && TemplateRenderer.Render(part, values, html: false).Trim() is { Length: > 0 } text ? text : null;

        var url = Text(TemplateParts.EmailAction);
        var email = new EmailParts(
            Text(TemplateParts.EmailSubject) ?? string.Empty,
            Text(TemplateParts.EmailPreheader),
            Text(TemplateParts.EmailHeading),
            TemplateRenderer.Render(parts[TemplateParts.EmailBody], values, html: true),
            EmailParts.ParseDetails(Text(TemplateParts.EmailDetails)),
            Text(TemplateParts.EmailButton),
            url is not null && Uri.TryCreate(url, UriKind.Absolute, out var link) && (string.Equals(link.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || string.Equals(link.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)) ? url : null);
        var brand = new EmailBrand(values.GetValueOrDefault(NotificationCatalog.AppNameVariable) ?? "Base");
        var text = Text(TemplateParts.EmailText) ?? EmailLayout.Text(email, richText.ToPlainText(email.BodyHtml), brand);
        return new RenderedContent(email.Subject, text, EmailLayout.Html(email, brand), null);
    }

    public static OutgoingMessage ToOutgoing(NotificationMessage message, NotificationTemplateDefinition template) => new(
        message.Id,
        message.Channel,
        message.RecipientAddress,
        message.RecipientName,
        message.Subject,
        message.Body,
        message.HtmlBody,
        message.Channel == NotificationChannel.WhatsApp ? template.WhatsApp : null,
        message.TemplateParameters is { } json ? JsonSerializer.Deserialize<string[]>(json, Json) ?? [] : []);

    public static OutgoingMessage ToOutgoing(Guid messageId, NotificationChannel channel, NotificationRecipientSnapshot recipient, RenderedContent content, NotificationTemplateDefinition template) => new(
        messageId,
        channel,
        recipient.AddressFor(channel)!,
        recipient.Name,
        content.Subject,
        content.Body,
        content.HtmlBody,
        channel == NotificationChannel.WhatsApp ? template.WhatsApp : null,
        content.TemplateParameters is { } json ? JsonSerializer.Deserialize<string[]>(json, Json) ?? [] : []);

    public static string SerializeChannels(IEnumerable<NotificationChannel> channels) => string.Join(',', channels);

    public static List<NotificationChannel> ParseChannels(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? [] : csv.Split(',').Select(c => Enum.Parse<NotificationChannel>(c)).ToList();
}
