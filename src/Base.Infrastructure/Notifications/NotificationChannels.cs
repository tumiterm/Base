using Base.Application.Notifications;
using Base.Application.Settings;
using Base.Domain.Notifications;
using Base.Infrastructure.Notifications.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Base.Infrastructure.Notifications;

/// <summary>
/// Channel switches come from configuration. The provider is, in order: the tenant's setting if it names a valid
/// instance; for email, the SMTP server from the Email settings when switched on; else the channel default.
/// </summary>
internal sealed partial class NotificationChannels(
    NotificationProviderRegistry registry,
    IOptions<NotificationOptions> options,
    IApplicationSettingService settings,
    ILogger<NotificationChannels> logger) : INotificationChannels
{
    /// <summary>The provider built from the Email settings (<c>Email.Smtp.*</c>).</summary>
    public const string SettingsSmtpName = "smtp-settings";

    public bool IsEnabled(NotificationChannel channel) => options.Value.For(channel).Enabled;

    public async Task<INotificationProvider?> ResolveAsync(NotificationChannel channel, CancellationToken cancellationToken = default) =>
        (await RouteAsync(channel, cancellationToken)).Provider;

    public async Task<ChannelRoute> DescribeAsync(NotificationChannel channel, CancellationToken cancellationToken = default)
    {
        var (provider, source) = await RouteAsync(channel, cancellationToken);
        if (provider is null)
        {
            return new ChannelRoute(false, null, false, IsEnabled(channel) ? "No usable provider is configured." : "Switched off in configuration.", ChannelRouteSource.Off);
        }

        var delivers = provider is not LogNotificationProvider;
        var description = provider switch
        {
            LogNotificationProvider => "Written to the application log, not sent.",
            IProviderEndpoint endpoint => $"Sent through {endpoint.Endpoint.Host}" + (endpoint.Endpoint.Port > 0 ? $":{endpoint.Endpoint.Port}" : "") + ".",
            _ => $"Sent through the {provider.Name} provider.",
        };
        return new ChannelRoute(true, provider.Name, delivers, description, source);
    }

    public IReadOnlyList<string> ProviderNames(NotificationChannel channel)
    {
        var names = registry.All.Where(p => p.Channel == channel).Select(p => p.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (channel == NotificationChannel.Email)
        {
            names.Insert(0, SettingsSmtpName);
        }

        return names;
    }

    private async Task<(INotificationProvider? Provider, ChannelRouteSource Source)> RouteAsync(NotificationChannel channel, CancellationToken cancellationToken)
    {
        if (!IsEnabled(channel) || channel == NotificationChannel.InApp)
        {
            return (null, ChannelRouteSource.Off);
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
            var chosen = channel == NotificationChannel.Email && string.Equals(tenantChoice, SettingsSmtpName, StringComparison.Ordinal)
                ? await SettingsSmtpAsync(cancellationToken)
                : registry.Find(tenantChoice, channel);
            if (chosen is not null)
            {
                return (chosen, ChannelRouteSource.TenantChoice);
            }

            LogUnknownTenantProvider(logger, tenantChoice, channel);
        }

        if (channel == NotificationChannel.Email && await settings.GetBoolAsync(SettingCodes.Email.SmtpEnabled, cancellationToken))
        {
            if (await SettingsSmtpAsync(cancellationToken) is { } smtp)
            {
                return (smtp, ChannelRouteSource.EmailSettings);
            }

            LogIncompleteSmtpSettings(logger);
        }

        return (registry.Find(options.Value.For(channel).Provider, channel), ChannelRouteSource.Configuration);
    }

    /// <summary>An SMTP provider from the Email settings, or null while the server or sender is missing.</summary>
    private async Task<INotificationProvider?> SettingsSmtpAsync(CancellationToken cancellationToken)
    {
        var host = await settings.GetAsync(SettingCodes.Email.SmtpHost, cancellationToken);
        var from = await settings.GetAsync(SettingCodes.Email.FromAddress, cancellationToken);
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from))
        {
            return null;
        }

        var fromName = await settings.GetAsync(SettingCodes.Email.FromName, cancellationToken);
        return new SmtpEmailProvider(SettingsSmtpName, new SmtpProviderOptions
        {
            Host = host,
            Port = await settings.GetIntAsync(SettingCodes.Email.SmtpPort, cancellationToken),
            UseStartTls = await settings.GetBoolAsync(SettingCodes.Email.SmtpUseStartTls, cancellationToken),
            UserName = await settings.GetAsync(SettingCodes.Email.SmtpUserName, cancellationToken),
            Password = await settings.GetAsync(SettingCodes.Email.SmtpPassword, cancellationToken),
            FromAddress = from,
            FromName = string.IsNullOrWhiteSpace(fromName) ? await settings.GetAsync(SettingCodes.General.ApplicationName, cancellationToken) ?? string.Empty : fromName,
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant setting names provider {Provider} for {Channel}, but no such instance is configured; using the channel default")]
    private static partial void LogUnknownTenantProvider(ILogger logger, string provider, NotificationChannel channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email settings switch SMTP on, but the server or the from address is missing; using the Email channel default")]
    private static partial void LogIncompleteSmtpSettings(ILogger logger);
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
