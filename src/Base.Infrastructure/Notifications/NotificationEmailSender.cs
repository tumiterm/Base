using Base.Application.Common.Abstractions;
using Base.Application.Notifications;
using Base.Domain.Common;
using Base.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace Base.Infrastructure.Notifications;

/// <summary>
/// Plain, untemplated email (e.g. operator error alerts) through the Email channel's provider, so all mail uses one
/// configuration. User-facing messages should use <see cref="INotificationService"/> instead.
/// </summary>
internal sealed partial class NotificationEmailSender(INotificationChannels channels, ILogger<NotificationEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var provider = await channels.ResolveAsync(NotificationChannel.Email, cancellationToken);
        if (provider is null)
        {
            LogNoProvider(logger, message.Subject);
            return;
        }

        foreach (var to in message.To)
        {
            var result = await provider.SendAsync(
                new OutgoingMessage(SequentialGuid.Next(), NotificationChannel.Email, to, null, message.Subject, message.TextBody, message.HtmlBody, null, []),
                cancellationToken);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"Email to {to} failed: {result.Error}");
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email channel is off; '{Subject}' was not sent")]
    private static partial void LogNoProvider(ILogger logger, string subject);
}
