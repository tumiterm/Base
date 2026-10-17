namespace Base.Domain.Notifications;

public enum NotificationChannel
{
    Email = 0,
    Sms = 1,
    WhatsApp = 2,
    InApp = 3,
}

public enum NotificationStatus
{
    /// <summary>Recorded; waiting for the outbox to deliver it.</summary>
    Queued = 0,

    /// <summary>A delivery attempt failed with a retryable error; it will be tried again.</summary>
    Retrying = 1,

    /// <summary>Accepted by the provider (or, for in-app, stored in the user's inbox).</summary>
    Sent = 2,

    /// <summary>Gave up: a permanent error, or the retries ran out.</summary>
    Failed = 3,

    /// <summary>Not sent on purpose: channel switched off, user opted out, or no address for the channel.</summary>
    Skipped = 4,
}
