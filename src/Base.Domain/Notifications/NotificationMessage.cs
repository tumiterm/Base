using Base.Domain.Auditing;
using Base.Domain.Common;

namespace Base.Domain.Notifications;

/// <summary>Raised when a message is queued; its handler delivers it after the transaction commits.</summary>
public sealed record NotificationQueued(Guid MessageId) : IDomainEvent;

/// <summary>
/// One message on one channel to one recipient: the delivery log, the in-app inbox and the resend source.
/// Content is stored as rendered, so a resend sends exactly what was sent before; sensitive messages (sign-in links)
/// keep no content at all.
/// </summary>
/// <remarks>Not audited: this table is itself the record of what was sent.</remarks>
[NotAudited]
public sealed class NotificationMessage : EntityBase, ITenantOwned
{
    public const int TemplateCodeMaxLength = 150;
    public const int CategoryMaxLength = 100;
    public const int AddressMaxLength = 320;
    public const int NameMaxLength = 200;
    public const int SubjectMaxLength = 500;
    public const int ProviderMaxLength = 100;
    public const int ProviderMessageIdMaxLength = 200;
    public const int ErrorMaxLength = 2000;

    private NotificationMessage()
    {
    }

    private NotificationMessage(
        string templateCode,
        string category,
        NotificationChannel channel,
        NotificationRecipientSnapshot recipient,
        string recipientAddress,
        RenderedContent content,
        bool isSensitive,
        NotificationReplay? replay,
        Guid? fallbackOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        Id = SequentialGuid.Next();
        TemplateCode = templateCode;
        Category = category;
        Channel = channel;
        RecipientUserId = recipient.UserId;
        RecipientAddress = recipientAddress;
        RecipientName = recipient.Name;
        IsSensitive = isSensitive;
        FallbackOf = fallbackOf;
        Status = NotificationStatus.Queued;

        if (!isSensitive)
        {
            Subject = content.Subject;
            Body = content.Body;
            HtmlBody = content.HtmlBody;
            TemplateParameters = content.TemplateParameters;
            DataJson = replay?.DataJson;
            RecipientJson = replay?.RecipientJson;
            FallbackChannels = replay?.FallbackChannels;
        }
    }

    public Guid TenantId { get; set; }

    public string TemplateCode { get; private set; } = string.Empty;

    public string Category { get; private set; } = string.Empty;

    public NotificationChannel Channel { get; private set; }

    public Guid? RecipientUserId { get; private set; }

    /// <summary>Email address or E.164 phone number; for in-app, the user id.</summary>
    public string RecipientAddress { get; private set; } = string.Empty;

    public string? RecipientName { get; private set; }

    public string? Subject { get; private set; }

    public string? Body { get; private set; }

    public string? HtmlBody { get; private set; }

    /// <summary>JSON array of approved-template parameters (WhatsApp).</summary>
    public string? TemplateParameters { get; private set; }

    /// <summary>The template variables (JSON), kept so a fallback channel can be rendered later. Null when sensitive.</summary>
    public string? DataJson { get; private set; }

    /// <summary>The recipient's addresses (JSON), kept so a fallback channel can reach them. Null when sensitive.</summary>
    public string? RecipientJson { get; private set; }

    /// <summary>Channels still to try, in order, if this one fails (comma-separated).</summary>
    public string? FallbackChannels { get; private set; }

    /// <summary>Content was not stored (it contained a secret, e.g. a password-reset link); it can't be resent.</summary>
    public bool IsSensitive { get; private set; }

    /// <summary>The message this one replaces after it failed (e.g. SMS after WhatsApp).</summary>
    public Guid? FallbackOf { get; private set; }

    public NotificationStatus Status { get; private set; }

    public string? Provider { get; private set; }

    public string? ProviderMessageId { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>In-app only: when the user read it.</summary>
    public DateTimeOffset? ReadAt { get; private set; }

    public bool IsFinal => Status is NotificationStatus.Sent or NotificationStatus.Failed or NotificationStatus.Skipped;

    /// <summary>Queues a message for delivery by the outbox after commit.</summary>
    public static NotificationMessage Queue(
        string templateCode,
        string category,
        NotificationChannel channel,
        NotificationRecipientSnapshot recipient,
        string recipientAddress,
        RenderedContent content,
        NotificationReplay? replay = null,
        Guid? fallbackOf = null)
    {
        var message = new NotificationMessage(templateCode, category, channel, recipient, recipientAddress, content, isSensitive: false, replay, fallbackOf);
        message.Raise(new NotificationQueued(message.Id));
        return message;
    }

    /// <summary>Records a sensitive message that the caller delivers immediately; its content is never stored.</summary>
    public static NotificationMessage RecordSensitive(
        string templateCode,
        string category,
        NotificationChannel channel,
        NotificationRecipientSnapshot recipient,
        string recipientAddress,
        Guid? fallbackOf = null) =>
        new(templateCode, category, channel, recipient, recipientAddress, RenderedContent.Empty, isSensitive: true, replay: null, fallbackOf);

    /// <summary>Records a message that was not sent (channel off, opted out, no address), so the log explains why.</summary>
    public static NotificationMessage RecordSkipped(
        string templateCode,
        string category,
        NotificationChannel channel,
        NotificationRecipientSnapshot recipient,
        string reason)
    {
        var message = new NotificationMessage(templateCode, category, channel, recipient, recipient.AddressFor(channel) ?? "-", RenderedContent.Empty, isSensitive: false, replay: null, fallbackOf: null);
        message.MarkSkipped(reason);
        return message;
    }

    /// <summary>A fresh queued copy for an administrator's resend. Sensitive messages can't be resent.</summary>
    public NotificationMessage CopyForResend()
    {
        if (IsSensitive)
        {
            throw new InvalidOperationException("Sensitive messages keep no content and can't be resent.");
        }

        // No fallback chain on a resend: the administrator chose this channel.
        return Queue(TemplateCode, Category, Channel, new NotificationRecipientSnapshot(RecipientUserId, RecipientName, null, null), RecipientAddress, Content,
            new NotificationReplay(DataJson, RecipientJson, null));
    }

    public RenderedContent Content => new(Subject, Body, HtmlBody, TemplateParameters);

    public void MarkSent(string provider, string? providerMessageId, DateTimeOffset at)
    {
        Attempts++;
        Status = NotificationStatus.Sent;
        Provider = Truncate(provider, ProviderMaxLength);
        ProviderMessageId = Truncate(providerMessageId, ProviderMessageIdMaxLength);
        SentAt = at;
        LastError = null;
    }

    public void MarkRetrying(string provider, string error)
    {
        Attempts++;
        Status = NotificationStatus.Retrying;
        Provider = Truncate(provider, ProviderMaxLength);
        LastError = Truncate(error, ErrorMaxLength);
    }

    public void MarkFailed(string? provider, string error)
    {
        Attempts++;
        Status = NotificationStatus.Failed;
        Provider = Truncate(provider, ProviderMaxLength);
        LastError = Truncate(error, ErrorMaxLength);
    }

    public void MarkSkipped(string reason)
    {
        Status = NotificationStatus.Skipped;
        LastError = Truncate(reason, ErrorMaxLength);
    }

    public void MarkRead(DateTimeOffset at) => ReadAt ??= at;

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } v && v.Length > max ? v[..max] : value;
}

/// <summary>What a channel sends: subject and bodies, or approved-template parameters (JSON array) for WhatsApp.</summary>
public sealed record RenderedContent(string? Subject, string? Body, string? HtmlBody, string? TemplateParameters)
{
    public static readonly RenderedContent Empty = new(null, null, null, null);
}

/// <summary>Who a message is for: an account and/or explicit addresses.</summary>
public sealed record NotificationRecipientSnapshot(Guid? UserId, string? Name, string? Email, string? Phone)
{
    public string? AddressFor(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Email => Email,
        NotificationChannel.Sms or NotificationChannel.WhatsApp => Phone,
        NotificationChannel.InApp => UserId?.ToString(),
        _ => null,
    };
}

/// <summary>What a queued message keeps so the dispatcher can try a fallback channel later.</summary>
public sealed record NotificationReplay(string? DataJson, string? RecipientJson, string? FallbackChannels);
