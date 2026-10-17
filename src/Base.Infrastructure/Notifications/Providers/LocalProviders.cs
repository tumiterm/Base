using Base.Application.Notifications;
using Base.Domain.Notifications;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Base.Infrastructure.Notifications.Providers;

/// <summary>
/// Writes messages to the log instead of sending them: the default for email, and handy for any channel in development.
/// Bodies are logged only with <c>IncludeBody: true</c> (development), because they can contain sign-in links.
/// </summary>
public sealed partial class LogNotificationProvider(string name, NotificationChannel channel, bool includeBody, ILogger<LogNotificationProvider> logger) : INotificationProvider
{
    public string Name { get; } = name;

    public NotificationChannel Channel { get; } = channel;

    public static IEnumerable<INotificationProvider> ForAllChannels(string name, IConfigurationSection? section, IServiceProvider services)
    {
        var includeBody = section?.GetValue<bool>("IncludeBody") ?? false;
        var logger = services.GetRequiredService<ILogger<LogNotificationProvider>>();
        return new[] { NotificationChannel.Email, NotificationChannel.Sms, NotificationChannel.WhatsApp }
            .Select(c => new LogNotificationProvider(name, c, includeBody, logger));
    }

    public Task<ProviderResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var content = !includeBody ? "(body not logged)"
            : message.Channel == NotificationChannel.WhatsApp ? $"template {message.WhatsApp?.Name} [{string.Join(" | ", message.TemplateParameters)}]"
            : message.Body;
        LogMessage(logger, message.Channel, message.To, message.Subject, content);
        return Task.FromResult(ProviderResult.Sent($"log-{message.MessageId:N}"));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Not sent ({Channel} goes to the log): to {To}, subject {Subject}\n{Content}")]
    private static partial void LogMessage(ILogger logger, NotificationChannel channel, string to, string? subject, string? content);
}

/// <summary>Email over SMTP (MailKit). Connection or server errors are retryable; a rejected recipient is not.</summary>
public sealed class SmtpEmailProvider(string name, SmtpProviderOptions options) : INotificationProvider, IProviderEndpoint
{
    public Uri Endpoint => new($"smtp://{options.Host}:{options.Port}");

    public string Name { get; } = name;

    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<ProviderResult> SendAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(options.FromName, options.FromAddress));
        mime.To.Add(new MailboxAddress(message.ToName ?? string.Empty, message.To));
        mime.Subject = message.Subject ?? string.Empty;
        mime.Body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.Body }.ToMessageBody();

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(options.Host, options.Port, options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, cancellationToken);
            if (!string.IsNullOrEmpty(options.UserName))
            {
                await client.AuthenticateAsync(options.UserName, options.Password ?? string.Empty, cancellationToken);
            }

            var response = await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
            return ProviderResult.Sent(mime.MessageId ?? response);
        }
        catch (SmtpCommandException exception) when (exception.ErrorCode is SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.SenderNotAccepted)
        {
            return ProviderResult.Failed(exception.Message, isRetryable: false);
        }
        catch (Exception exception) when (exception is SmtpCommandException or SmtpProtocolException or System.Net.Sockets.SocketException or IOException or AuthenticationException)
        {
            return ProviderResult.Failed(exception.Message, isRetryable: exception is not AuthenticationException);
        }
    }
}
