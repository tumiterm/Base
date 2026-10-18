using Base.Domain.Auditing;
using Base.Domain.Common;

namespace Base.Domain.Communications;

public enum BroadcastStatus
{
    /// <summary>Waiting for its time; can still be changed or cancelled.</summary>
    Scheduled = 0,

    /// <summary>Picked up; messages are being queued.</summary>
    Sending = 1,

    /// <summary>Every recipient's messages are queued (or skipped, with the reason); delivery is tracked per message.</summary>
    Sent = 2,

    Cancelled = 3,

    /// <summary>Stopped before every message was queued (the sender lost access, the plan's limit was reached...).</summary>
    Failed = 4,
}

/// <summary>Who a broadcast goes to.</summary>
public enum BroadcastAudienceKind
{
    /// <summary>People picked one by one from an audience (keys).</summary>
    Chosen = 0,

    /// <summary>Everyone an audience's filters match, worked out when it is sent.</summary>
    Filtered = 1,

    /// <summary>A pasted or uploaded list, stored with the broadcast.</summary>
    List = 2,
}

/// <summary>Raised when a broadcast is due now; its handler sends it after the transaction commits.</summary>
public sealed record BroadcastDue(Guid BroadcastId) : IDomainEvent;

/// <summary>
/// A message written by a person and sent to one or many recipients on one or more channels, now or at a set time.
/// Delivery to each recipient is a notification message linked back to it (<c>NotificationMessage.SourceId</c>).
/// </summary>
public sealed class Broadcast : EntityBase, ITenantOwned
{
    public const int TitleMaxLength = 200;
    public const int SubjectMaxLength = 500;
    public const int AudienceMaxLength = 100;
    public const int AudienceLabelMaxLength = 500;
    public const int ChannelsMaxLength = 100;
    public const int WhatsAppNameMaxLength = 200;
    public const int WhatsAppLanguageMaxLength = 20;
    public const int ErrorMaxLength = 2000;
    public const int NameMaxLength = 200;

    private Broadcast()
    {
    }

    public Broadcast(
        string title,
        BroadcastAudienceKind audienceKind,
        string audienceCode,
        string audienceLabel,
        string? audienceJson,
        Guid senderUserId,
        string senderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(audienceCode);
        Id = SequentialGuid.Next();
        Title = Limit(title.Trim(), TitleMaxLength);
        AudienceKind = audienceKind;
        AudienceCode = audienceCode;
        AudienceLabel = Limit(audienceLabel, AudienceLabelMaxLength);
        AudienceJson = audienceJson;
        SenderUserId = senderUserId;
        SenderName = Limit(senderName, NameMaxLength);
        Status = BroadcastStatus.Scheduled;
    }

    public Guid TenantId { get; set; }

    /// <summary>What the sender calls it ("June newsletter"); recipients don't see it.</summary>
    public string Title { get; private set; } = string.Empty;

    public BroadcastAudienceKind AudienceKind { get; private set; }

    /// <summary>The audience source (<c>people</c>, <c>users</c>, or <c>list</c> for a pasted list).</summary>
    public string AudienceCode { get; private set; } = string.Empty;

    /// <summary>How it reads on screen: "People · Department: Finance".</summary>
    public string AudienceLabel { get; private set; } = string.Empty;

    /// <summary>Filters (Filtered), keys (Chosen) or the rows (List), as JSON. A pasted list holds contact details.</summary>
    [AuditRedacted]
    public string? AudienceJson { get; private set; }

    /// <summary>Comma-separated channels (Email, Sms, WhatsApp, InApp).</summary>
    public string Channels { get; private set; } = string.Empty;

    public string? Subject { get; private set; }

    /// <summary>The email body, cleaned of anything unsafe when it was saved.</summary>
    public string? HtmlBody { get; private set; }

    /// <summary>Plain text of the email body; also the in-app message.</summary>
    public string? TextBody { get; private set; }

    public string? SmsBody { get; private set; }

    public string? WhatsAppTemplateName { get; private set; }

    public string? WhatsAppLanguage { get; private set; }

    /// <summary>Text for the approved template's {{1}}, {{2}}..., as a JSON array; each may use placeholders.</summary>
    public string? WhatsAppParameters { get; private set; }

    public BroadcastStatus Status { get; private set; }

    /// <summary>When it goes out (UTC). The time it was due when sent straight away.</summary>
    public DateTimeOffset ScheduledFor { get; private set; }

    /// <summary>Set for a later time: the scheduler sends it. Otherwise the outbox sends it as soon as it is saved.</summary>
    public bool IsScheduled { get; private set; }

    /// <summary>Sent as this user: their permissions decide who the audience includes.</summary>
    public Guid SenderUserId { get; private set; }

    public string SenderName { get; private set; } = string.Empty;

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>People it went to after removing duplicates.</summary>
    public int RecipientCount { get; private set; }

    /// <summary>Messages queued for delivery, over every channel.</summary>
    public int QueuedCount { get; private set; }

    /// <summary>Messages not sent (no address, opted out, channel off), each with its reason in the log.</summary>
    public int SkippedCount { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Recipients handled so far, in audience order: a send that was interrupted carries on from here.</summary>
    public int Processed { get; private set; }

    public bool CanChange => Status == BroadcastStatus.Scheduled;

    public void SetContent(string channels, string? subject, string? htmlBody, string? textBody, string? smsBody, string? whatsAppTemplateName, string? whatsAppLanguage, string? whatsAppParameters)
    {
        EnsureCanChange();
        ArgumentException.ThrowIfNullOrWhiteSpace(channels);
        Channels = Limit(channels, ChannelsMaxLength);
        Subject = Optional(subject, SubjectMaxLength);
        HtmlBody = string.IsNullOrWhiteSpace(htmlBody) ? null : htmlBody;
        TextBody = string.IsNullOrWhiteSpace(textBody) ? null : textBody;
        SmsBody = string.IsNullOrWhiteSpace(smsBody) ? null : smsBody;
        WhatsAppTemplateName = Optional(whatsAppTemplateName, WhatsAppNameMaxLength);
        WhatsAppLanguage = Optional(whatsAppLanguage, WhatsAppLanguageMaxLength);
        WhatsAppParameters = string.IsNullOrWhiteSpace(whatsAppParameters) ? null : whatsAppParameters;
    }

    public void SetAudience(BroadcastAudienceKind kind, string audienceCode, string audienceLabel, string? audienceJson)
    {
        EnsureCanChange();
        ArgumentException.ThrowIfNullOrWhiteSpace(audienceCode);
        AudienceKind = kind;
        AudienceCode = audienceCode;
        AudienceLabel = Limit(audienceLabel, AudienceLabelMaxLength);
        AudienceJson = audienceJson;
    }

    public void Rename(string title)
    {
        EnsureCanChange();
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Title = Limit(title.Trim(), TitleMaxLength);
    }

    /// <summary>Goes out at <paramref name="at"/>; the scheduler picks it up then.</summary>
    public void ScheduleFor(DateTimeOffset at)
    {
        EnsureCanChange();
        ScheduledFor = at.ToUniversalTime();
        IsScheduled = true;
    }

    /// <summary>Due now: sent by the outbox straight after this commits.</summary>
    public void SendNow(DateTimeOffset now)
    {
        EnsureCanChange();
        ScheduledFor = now.ToUniversalTime();
        IsScheduled = false;
        Raise(new BroadcastDue(Id));
    }

    /// <summary>
    /// Claims it for sending, or picks an interrupted send back up. False when it is finished or cancelled. Two senders
    /// can't both carry on: the row version rejects the second one's save.
    /// </summary>
    public bool TryStart(DateTimeOffset now)
    {
        if (Status == BroadcastStatus.Sending)
        {
            return true;
        }

        if (Status != BroadcastStatus.Scheduled)
        {
            return false;
        }

        Status = BroadcastStatus.Sending;
        StartedAt = now;
        return true;
    }

    /// <summary>One more recipient handled; saved with that recipient's messages.</summary>
    public void Advance() => Processed++;

    public void Complete(int recipients, int queued, int skipped, DateTimeOffset now)
    {
        Status = BroadcastStatus.Sent;
        RecipientCount = recipients;
        QueuedCount = queued;
        SkippedCount = skipped;
        CompletedAt = now;
        Error = null;
    }

    public void Fail(string error, int recipients, int queued, int skipped, DateTimeOffset now)
    {
        Status = BroadcastStatus.Failed;
        RecipientCount = recipients;
        QueuedCount = queued;
        SkippedCount = skipped;
        CompletedAt = now;
        Error = Limit(error, ErrorMaxLength);
    }

    public void Cancel()
    {
        EnsureCanChange();
        Status = BroadcastStatus.Cancelled;
    }

    private void EnsureCanChange()
    {
        if (!CanChange)
        {
            throw new InvalidOperationException("Only a scheduled message can be changed.");
        }
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? Optional(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : Limit(value.Trim(), max);
}
