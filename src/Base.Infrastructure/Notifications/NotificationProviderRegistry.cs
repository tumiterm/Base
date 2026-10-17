using Base.Application.Notifications;
using Base.Domain.Notifications;
using Base.Infrastructure.Notifications.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Base.Infrastructure.Notifications;

/// <summary>
/// Every provider instance, by name: built from <c>Notifications:Providers</c> (by <c>Type</c>), plus any
/// <see cref="INotificationProvider"/> a project registers in DI itself, plus the built-in <c>log</c>.
/// </summary>
public sealed class NotificationProviderRegistry
{
    /// <summary>Provider types available from configuration. Add your own with <see cref="INotificationProvider"/> in DI.</summary>
    public static readonly IReadOnlyDictionary<string, Func<string, IConfigurationSection, IServiceProvider, IEnumerable<INotificationProvider>>> Types =
        new Dictionary<string, Func<string, IConfigurationSection, IServiceProvider, IEnumerable<INotificationProvider>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Log"] = (name, section, sp) => LogNotificationProvider.ForAllChannels(name, section, sp),
            ["Smtp"] = (name, section, sp) => [ActivatorUtilities.CreateInstance<SmtpEmailProvider>(sp, name, ProviderOptions.Bind<SmtpProviderOptions>(section))],
            ["SendGrid"] = (name, section, sp) => [ActivatorUtilities.CreateInstance<SendGridEmailProvider>(sp, name, ProviderOptions.Bind<SendGridOptions>(section))],
            ["TwilioSms"] = (name, section, sp) => [ActivatorUtilities.CreateInstance<TwilioSmsProvider>(sp, name, ProviderOptions.Bind<TwilioOptions>(section))],
            ["TwilioWhatsApp"] = (name, section, sp) => [ActivatorUtilities.CreateInstance<TwilioWhatsAppProvider>(sp, name, ProviderOptions.Bind<TwilioOptions>(section))],
            ["Clickatell"] = (name, section, sp) => [ActivatorUtilities.CreateInstance<ClickatellSmsProvider>(sp, name, ProviderOptions.Bind<ClickatellOptions>(section))],
            ["BulkSms"] = (name, section, sp) => [ActivatorUtilities.CreateInstance<BulkSmsProvider>(sp, name, ProviderOptions.Bind<BulkSmsOptions>(section))],
            ["MetaWhatsApp"] = (name, section, sp) => [ActivatorUtilities.CreateInstance<MetaWhatsAppProvider>(sp, name, ProviderOptions.Bind<MetaWhatsAppOptions>(section))],
        };

    private readonly Dictionary<(string Name, NotificationChannel Channel), INotificationProvider> _providers = [];

    public NotificationProviderRegistry(IConfiguration configuration, IServiceProvider services, IEnumerable<INotificationProvider> registered)
    {
        foreach (var provider in LogNotificationProvider.ForAllChannels(NotificationOptions.LogProviderName, null, services))
        {
            _providers[(provider.Name, provider.Channel)] = provider;
        }

        foreach (var section in configuration.GetSection($"{NotificationOptions.SectionName}:Providers").GetChildren())
        {
            var type = section["Type"];
            if (string.IsNullOrWhiteSpace(type) || !Types.TryGetValue(type, out var create))
            {
                throw new InvalidOperationException(
                    $"Notifications:Providers:{section.Key}:Type is '{type}'. Known types: {string.Join(", ", Types.Keys)}.");
            }

            foreach (var provider in create(section.Key, section, services))
            {
                _providers[(provider.Name, provider.Channel)] = provider;
            }
        }

        foreach (var provider in registered)
        {
            _providers[(provider.Name, provider.Channel)] = provider;
        }
    }

    public INotificationProvider? Find(string? name, NotificationChannel channel) =>
        name is not null && _providers.TryGetValue((name, channel), out var provider) ? provider : null;

    public IEnumerable<(string Name, NotificationChannel Channel)> All => _providers.Keys;
}

internal static class ProviderOptions
{
    /// <summary>Binds a provider's section and fails fast, naming the missing keys.</summary>
    public static T Bind<T>(IConfigurationSection section)
        where T : IValidatableProviderOptions, new()
    {
        var options = section.Get<T>() ?? new T();
        var missing = options.MissingSettings().ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Notification provider '{section.Key}' is missing: {string.Join(", ", missing.Select(m => $"Notifications:Providers:{section.Key}:{m}"))}.");
        }

        return options;
    }
}

public interface IValidatableProviderOptions
{
    IEnumerable<string> MissingSettings();
}
