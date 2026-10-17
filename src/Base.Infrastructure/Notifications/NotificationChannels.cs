using Base.Application.Notifications;
using Base.Application.Settings;
using Base.Domain.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Base.Infrastructure.Notifications;

/// <summary>Channel switches come from configuration; the provider is the tenant's setting if it names a valid instance, else the channel default.</summary>
internal sealed partial class NotificationChannels(
    NotificationProviderRegistry registry,
    IOptions<NotificationOptions> options,
    IApplicationSettingService settings,
    ILogger<NotificationChannels> logger) : INotificationChannels
{
    public bool IsEnabled(NotificationChannel channel) => options.Value.For(channel).Enabled;

    public async Task<INotificationProvider?> ResolveAsync(NotificationChannel channel, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled(channel) || channel == NotificationChannel.InApp)
        {
            return null;
        }

        var settingCode = channel switch
        {
            NotificationChannel.Email => SettingCodes.Notifications.EmailProvider,
            NotificationChannel.Sms => SettingCodes.Notifications.SmsProvider,
            _ => SettingCodes.Notifications.WhatsAppProvider,
        };

        var tenantChoice = await settings.GetAsync(settingCode, cancellationToken);
        if (!string.IsNullOrWhiteSpace(tenantChoice))
        {
            if (registry.Find(tenantChoice, channel) is { } chosen)
            {
                return chosen;
            }

            LogUnknownTenantProvider(logger, tenantChoice, channel);
        }

        return registry.Find(options.Value.For(channel).Provider, channel);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant setting names provider {Provider} for {Channel}, but no such instance is configured; using the channel default")]
    private static partial void LogUnknownTenantProvider(ILogger logger, string provider, NotificationChannel channel);
}

/// <summary>Checks at startup that every switched-on channel points at a configured provider of the right kind.</summary>
internal sealed class NotificationConfigurationCheck(NotificationProviderRegistry registry, IOptions<NotificationOptions> options) : Startup.IStartupTask
{
    public int Order => 5;

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        foreach (var channel in new[] { NotificationChannel.Email, NotificationChannel.Sms, NotificationChannel.WhatsApp })
        {
            var configured = options.Value.For(channel);
            if (configured.Enabled && registry.Find(configured.Provider, channel) is null)
            {
                var available = registry.All.Where(p => p.Channel == channel).Select(p => p.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
                throw new InvalidOperationException(
                    $"Notifications:Channels:{channel} is enabled with Provider '{configured.Provider}', which is not a configured {channel} provider. Available: {string.Join(", ", available)}.");
            }
        }

        return Task.CompletedTask;
    }
}
