using Base.Domain.Communications;
using Base.Domain.Notifications;

namespace Base.Application.Communications;

/// <summary>An audience the current user may send to, with what it offers.</summary>
/// <param name="Bulk">Whether the user may send to everyone its filters match (needs Bulk messages).</param>
public sealed record AudienceView(string Code, string Name, string Description, string Icon, IReadOnlyList<AudienceField> Fields, IReadOnlyList<AudienceFilter> Filters, bool Bulk);

/// <summary>Who a message goes to.</summary>
/// <param name="Filters">Filtered: filter code to value (empty values are ignored).</param>
/// <param name="Keys">Chosen: the members picked one by one.</param>
/// <param name="ListText">List: the pasted or uploaded CSV, header row first.</param>
public sealed record AudienceChoice(
    BroadcastAudienceKind Kind,
    string AudienceCode,
    IReadOnlyDictionary<string, string>? Filters = null,
    IReadOnlyList<string>? Keys = null,
    string? ListText = null);

/// <summary>Everything a message is: wording, channels, audience and when.</summary>
/// <param name="WhatsAppParameters">Text for the approved template's {{1}}, {{2}}..., each may use placeholders.</param>
/// <param name="SendAt">Null sends it now; otherwise the time it goes out.</param>
public sealed record MessageDraft(
    string? Title,
    IReadOnlyList<NotificationChannel> Channels,
    string? Subject,
    string? HtmlBody,
    string? SmsBody,
    string? WhatsAppTemplateName,
    string? WhatsAppLanguage,
    IReadOnlyList<string>? WhatsAppParameters,
    AudienceChoice Audience,
    DateTimeOffset? SendAt = null);

/// <summary>How many an audience choice reaches, and on which channels.</summary>
/// <param name="WithAccount">Members who sign in: the only ones in-app messages reach.</param>
/// <param name="Problems">Things to fix in a pasted list (rows without an address...).</param>
public sealed record AudienceReach(
    string Label,
    int People,
    int WithEmail,
    int WithPhone,
    int WithAccount,
    IReadOnlyList<AudienceMember> Sample,
    IReadOnlyList<AudienceField> Fields,
    IReadOnlyList<string> Problems);

/// <summary>What one recipient would receive on each channel.</summary>
public sealed record MessagePreview(AudienceMember? For, string? Subject, string? Html, string? Text, string? Sms, int SmsParts, IReadOnlyList<string> WhatsAppParameters);

public sealed record BroadcastSummary(
    Guid Id,
    string Title,
    string AudienceLabel,
    IReadOnlyList<NotificationChannel> Channels,
    BroadcastStatus Status,
    DateTimeOffset ScheduledFor,
    bool IsScheduled,
    string SenderName,
    int RecipientCount,
    int QueuedCount,
    int SkippedCount);

public sealed record BroadcastQuery(BroadcastStatus? Status = null, int Page = 1, int PageSize = 25);

/// <summary>Delivery so far, from the messages themselves (queued includes those being retried).</summary>
public sealed record DeliveryCounts(int Queued, int Sent, int Failed, int Skipped)
{
    public int Total => Queued + Sent + Failed + Skipped;
}

public sealed record BroadcastDetails(
    BroadcastSummary Summary,
    MessageDraft Draft,
    string? HtmlBody,
    string? TextBody,
    DeliveryCounts Delivery,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error);

/// <summary>One message of a broadcast: one person on one channel.</summary>
public sealed record BroadcastDelivery(Guid MessageId, string? Name, string Address, NotificationChannel Channel, NotificationStatus Status, string? Error, DateTimeOffset? SentAt);

public sealed record MessageTemplateView(
    Guid Id,
    string Name,
    string? Subject,
    string? HtmlBody,
    string? SmsBody,
    string? WhatsAppTemplateName,
    string? WhatsAppLanguage,
    IReadOnlyList<string> WhatsAppParameters,
    DateTimeOffset DateModified);

public sealed record SaveMessageTemplateRequest(
    string Name,
    string? Subject,
    string? HtmlBody,
    string? SmsBody,
    string? WhatsAppTemplateName = null,
    string? WhatsAppLanguage = null,
    IReadOnlyList<string>? WhatsAppParameters = null);
