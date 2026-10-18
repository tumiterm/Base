using Base.Application.Common.Persistence;
using Base.Application.Communications;
using Base.Domain.Communications;
using Base.Domain.Notifications;

namespace Base.Web.ViewModels;

public sealed record MessageListViewModel(PagedResult<BroadcastSummary> Messages, BroadcastStatus? Status);

/// <summary>The composer form as posted. <see cref="Mode"/> picks how the audience is chosen.</summary>
public sealed class ComposeForm
{
    public const string Pick = "pick";
    public const string Everyone = "everyone";
    public const string Paste = "list";

    public string? Title { get; set; }

    public IList<NotificationChannel> Channels { get; set; } = [NotificationChannel.Email];

    public string? Subject { get; set; }

    public string? HtmlBody { get; set; }

    public string? SmsBody { get; set; }

    public string? WhatsAppTemplateName { get; set; }

    public string? WhatsAppLanguage { get; set; } = "en";

    public IList<string> WhatsAppParameters { get; set; } = [];

    public string AudienceCode { get; set; } = AudienceCodes.People;

    public string Mode { get; set; } = Pick;

    public IList<string> Keys { get; set; } = [];

    /// <summary>Filters as "audience:filter" to value, so each audience's filters keep their own names.</summary>
    public IDictionary<string, string> Filters { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public string? ListText { get; set; }

    public IFormFile? ListFile { get; set; }

    /// <summary>"now" or "later".</summary>
    public string When { get; set; } = "now";

    /// <summary>The organisation's local time, from a datetime-local input.</summary>
    public DateTime? SendAtLocal { get; set; }

    /// <summary>"send", "preview" or "test".</summary>
    public string? Action { get; set; }

    public Guid? TemplateId { get; set; }

    public AudienceChoice Audience() => Mode switch
    {
        Paste => new AudienceChoice(BroadcastAudienceKind.List, AudienceCodes.List, ListText: ListText),
        Everyone => new AudienceChoice(BroadcastAudienceKind.Filtered, AudienceCode, Filters: Filters
            .Where(f => f.Key.StartsWith(AudienceCode + ":", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(f.Value))
            .ToDictionary(f => f.Key[(AudienceCode.Length + 1)..], f => f.Value, StringComparer.Ordinal)),
        _ => new AudienceChoice(BroadcastAudienceKind.Chosen, AudienceCode, Keys: Keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToList()),
    };

    public MessageDraft ToDraft(DateTimeOffset? sendAt) => new(
        Title,
        Channels.Distinct().ToList(),
        Subject,
        HtmlBody,
        SmsBody,
        WhatsAppTemplateName,
        WhatsAppLanguage,
        [.. WhatsAppParameters],
        Audience(),
        string.Equals(When, "later", StringComparison.Ordinal) ? sendAt : null);

    public static ComposeForm From(MessageDraft draft, DateTime? localSendAt)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var audience = draft.Audience;
        return new ComposeForm
        {
            Title = draft.Title,
            Channels = [.. draft.Channels],
            Subject = draft.Subject,
            HtmlBody = draft.HtmlBody,
            SmsBody = draft.SmsBody,
            WhatsAppTemplateName = draft.WhatsAppTemplateName,
            WhatsAppLanguage = draft.WhatsAppLanguage ?? "en",
            WhatsAppParameters = [.. draft.WhatsAppParameters ?? []],
            AudienceCode = audience.Kind == BroadcastAudienceKind.List ? AudienceCodes.People : audience.AudienceCode,
            Mode = audience.Kind switch
            {
                BroadcastAudienceKind.List => Paste,
                BroadcastAudienceKind.Filtered => Everyone,
                _ => Pick,
            },
            Keys = [.. audience.Keys ?? []],
            Filters = (audience.Filters ?? new Dictionary<string, string>(StringComparer.Ordinal)).ToDictionary(f => audience.AudienceCode + ":" + f.Key, f => f.Value, StringComparer.Ordinal),
            ListText = audience.ListText,
            When = localSendAt is null ? "now" : "later",
            SendAtLocal = localSendAt,
        };
    }
}

/// <param name="Chosen">The people picked so far, to show as chips.</param>
/// <param name="ChannelsOn">Which channels the organisation has switched on.</param>
/// <param name="EditingId">Set when changing a scheduled message.</param>
public sealed record ComposeViewModel(
    ComposeForm Form,
    IReadOnlyList<AudienceView> Audiences,
    IReadOnlyList<MessageTemplateView> Templates,
    IReadOnlyDictionary<NotificationChannel, bool> ChannelsOn,
    IReadOnlyList<AudienceMember> Chosen,
    AudienceReach? Reach,
    MessagePreview? Preview,
    string TimeZoneName,
    Guid? EditingId);

public sealed record MessageDetailsViewModel(BroadcastDetails Message, PagedResult<BroadcastDelivery> Deliveries, NotificationStatus? Status, DateTime? LocalScheduledFor);

public sealed class TemplateForm
{
    public Guid? Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Subject { get; set; }

    public string? HtmlBody { get; set; }

    public string? SmsBody { get; set; }

    public string? WhatsAppTemplateName { get; set; }

    public string? WhatsAppLanguage { get; set; } = "en";

    public IList<string> WhatsAppParameters { get; set; } = [];

    public SaveMessageTemplateRequest ToRequest() =>
        new(Name, Subject, HtmlBody, SmsBody, WhatsAppTemplateName, WhatsAppLanguage, WhatsAppParameters.Where(p => !string.IsNullOrWhiteSpace(p)).ToList());

    public static TemplateForm From(MessageTemplateView t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return new TemplateForm
        {
            Id = t.Id,
            Name = t.Name,
            Subject = t.Subject,
            HtmlBody = t.HtmlBody,
            SmsBody = t.SmsBody,
            WhatsAppTemplateName = t.WhatsAppTemplateName,
            WhatsAppLanguage = t.WhatsAppLanguage ?? "en",
            WhatsAppParameters = [.. t.WhatsAppParameters],
        };
    }
}
