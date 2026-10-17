using Base.Application.Common.Results;
using Base.Application.Settings;
using Base.Domain.Notifications;

namespace Base.Application.Notifications;

/// <summary>
/// Sends one test email straight through the provider email is routed to now (no queue, no retries), so the
/// settings screen can say at once whether delivery works and, if not, what the server answered.
/// </summary>
public interface IEmailDeliveryTest
{
    Task<ValidationResponse> SendAsync(string to, CancellationToken cancellationToken = default);
}

internal sealed class EmailDeliveryTest(INotificationChannels channels, IApplicationSettingService settings, TimeProvider time) : IEmailDeliveryTest
{
    public async Task<ValidationResponse> SendAsync(string to, CancellationToken cancellationToken = default)
    {
        var address = to?.Trim() ?? string.Empty;
        // The From address setting validates email addresses; reuse its rule.
        if (address.Length == 0 || SettingCatalog.Get(SettingCodes.Email.FromAddress).Validate(address).IsError)
        {
            return ValidationResponse.Failure(new ValidationError("to", "Enter the email address to send the test to.", "email.invalid"));
        }

        var route = await channels.DescribeAsync(NotificationChannel.Email, cancellationToken);
        var provider = await channels.ResolveAsync(NotificationChannel.Email, cancellationToken);
        if (provider is null)
        {
            return ValidationResponse.Failure($"Email can't be sent: {route.Description}");
        }

        var appName = await settings.GetAsync(SettingCodes.General.ApplicationName, cancellationToken) ?? "Base";
        var sentAt = time.GetUtcNow().ToString("u", System.Globalization.CultureInfo.InvariantCulture);
        var text = $"This is a test email from {appName}, sent at {sentAt} through {provider.Name}. If you can read it, email delivery works.";
        var message = new OutgoingMessage(
            Guid.NewGuid(), NotificationChannel.Email, address, null, $"{appName}: test email", text,
            $"<p>This is a test email from <strong>{System.Net.WebUtility.HtmlEncode(appName)}</strong>, sent at {sentAt} through {System.Net.WebUtility.HtmlEncode(provider.Name)}.</p><p>If you can read it, email delivery works.</p>",
            null, []);

        var result = await provider.SendAsync(message, cancellationToken);
        if (!result.Succeeded)
        {
            return ValidationResponse.Failure($"The test email wasn't sent. The server said: {result.Error}");
        }

        return route.Delivers
            ? ValidationResponse.Success($"Test email sent to {address}. Check the inbox (and the spam folder).")
            : ValidationResponse.Success($"The test email went to the application log, not to {address}: email isn't being delivered yet. Switch on the SMTP server below to send for real.");
    }
}
