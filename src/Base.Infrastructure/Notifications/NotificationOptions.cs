using Base.Domain.Notifications;

namespace Base.Infrastructure.Notifications;

/// <summary>
/// <c>Notifications</c> configuration. Channels are switched on one by one; each names its default provider instance.
/// Provider instances live under <c>Notifications:Providers:{name}</c> with a <c>Type</c> and that type's settings.
/// Credentials belong in user-secrets or environment variables, never in committed appsettings.
/// </summary>
/// <example>
/// <code>
/// "Notifications": {
///   "Channels": { "Sms": { "Enabled": true, "Provider": "clickatell" } },
///   "Providers": { "clickatell": { "Type": "Clickatell", "ApiKey": "(secret)" } }
/// }
/// </code>
/// </example>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    public IDictionary<NotificationChannel, ChannelOptions> Channels { get; set; } = new Dictionary<NotificationChannel, ChannelOptions>();

    public ChannelOptions For(NotificationChannel channel) =>
        Channels.TryGetValue(channel, out var configured) ? configured : Defaults(channel);

    /// <summary>Out of the box: email (to the log) and in-app are on; SMS and WhatsApp are off until configured.</summary>
    public static ChannelOptions Defaults(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Email => new ChannelOptions { Enabled = true, Provider = LogProviderName },
        NotificationChannel.InApp => new ChannelOptions { Enabled = true },
        _ => new ChannelOptions { Enabled = false },
    };

    public const string LogProviderName = "log";

    public sealed class ChannelOptions
    {
        public bool Enabled { get; set; }

        /// <summary>Default provider instance for the channel; a tenant can choose another with a setting.</summary>
        public string? Provider { get; set; }
    }
}
