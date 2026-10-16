using Base.Application.Common.Abstractions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Base.Infrastructure.Email;

internal sealed partial class MailKitEmailSender(IOptions<SmtpOptions> options, ILogger<MailKitEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var settings = options.Value;

        using var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        foreach (var to in message.To)
        {
            mime.To.Add(MailboxAddress.Parse(to));
        }

        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(settings.Host!, settings.Port, settings.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, cancellationToken);
        if (!string.IsNullOrEmpty(settings.UserName))
        {
            await client.AuthenticateAsync(settings.UserName, settings.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
        LogSent(logger, message.Subject, message.To.Count);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent email '{Subject}' to {RecipientCount} recipient(s)")]
    private static partial void LogSent(ILogger logger, string subject, int recipientCount);
}

/// <summary>Development fallback when no SMTP host is configured. Logs the subject and recipients, never the body.</summary>
internal sealed partial class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        LogEmail(logger, message.Subject, message.To);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SMTP not configured; email '{Subject}' to {Recipients} was not sent")]
    private static partial void LogEmail(ILogger logger, string subject, IReadOnlyList<string> recipients);
}
