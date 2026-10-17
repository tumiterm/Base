using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Base.Application.Common.Abstractions;
using Base.Application.Operations;
using Base.Application.Settings;
using Microsoft.Extensions.Caching.Memory;

namespace Base.Web.Errors;

/// <summary>A snapshot of an unhandled error: plain data, captured during the request so nothing holds on to HttpContext.</summary>
internal sealed record ErrorAlert(
    string Ticket,
    string CorrelationId,
    string Fingerprint,
    string ExceptionType,
    string Message,
    string Details,
    string Method,
    string Path,
    string Query,
    string? User,
    string? Tenant,
    string Environment,
    DateTimeOffset OccurredAt,
    Guid? TenantId = null,
    string? TraceId = null)
{
    /// <summary>Same exception type thrown from the same method = same fingerprint, so a storm sends one email.</summary>
    public static string FingerprintOf(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var site = exception.TargetSite is { } method ? $"{method.DeclaringType?.FullName}.{method.Name}" : "unknown";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{exception.GetType().FullName}|{site}"));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}

/// <summary>Bounded in-memory queue between the exception handler and the alert sender. Drops the oldest when full.</summary>
internal sealed class ErrorAlertQueue
{
    private readonly Channel<ErrorAlert> _channel = Channel.CreateBounded<ErrorAlert>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public bool TryEnqueue(ErrorAlert alert) => _channel.Writer.TryWrite(alert);

    public IAsyncEnumerable<ErrorAlert> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// Records every queued error in the error log (searchable by ticket on the system page), then emails it to the
/// configured recipients, at most once per fingerprint per throttle window. Runs outside the request.
/// </summary>
internal sealed partial class ErrorAlertSender(
    ErrorAlertQueue queue,
    IServiceScopeFactory scopes,
    IMemoryCache throttle,
    ILogger<ErrorAlertSender> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var alert in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RecordAsync(alert, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The database may be what failed; the log line from the handler still has everything.
                LogRecordFailed(logger, exception, alert.Ticket);
            }

            try
            {
                await SendAsync(alert, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogSendFailed(logger, exception, alert.Ticket);
            }
        }
    }

    private async Task RecordAsync(ErrorAlert alert, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IErrorLog>().RecordAsync(
            new ErrorRecord(
                alert.Ticket, alert.OccurredAt, alert.ExceptionType, alert.Message, alert.Method, alert.Path + alert.Query, alert.User,
                alert.TenantId, alert.TraceId, alert.Fingerprint, alert.Details),
            cancellationToken);
    }

    private async Task SendAsync(ErrorAlert alert, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<IApplicationSettingService>();
        var recipients = await settings.GetListAsync(SettingCodes.Notifications.ErrorAlertRecipients, cancellationToken);
        if (recipients.Count == 0)
        {
            return;
        }

        var minutes = await settings.GetIntAsync(SettingCodes.Notifications.ErrorAlertThrottleMinutes, cancellationToken);
        if (throttle.TryGetValue(alert.Fingerprint, out _))
        {
            LogThrottled(logger, alert.Ticket, alert.Fingerprint);
            return;
        }

        throttle.Set(alert.Fingerprint, true, TimeSpan.FromMinutes(minutes));

        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        await sender.SendAsync(
            new EmailMessage(recipients, $"[{alert.Environment}] {alert.ExceptionType} ({alert.Ticket})", RenderHtml(alert)),
            cancellationToken);
    }

    // Every value is HTML-encoded: exception messages can contain user input.
    private static string RenderHtml(ErrorAlert alert)
    {
        static string Row(string label, string? value) =>
            $"<tr><th align=\"left\">{label}</th><td>{WebUtility.HtmlEncode(value ?? "-")}</td></tr>";

        return new StringBuilder()
            .Append("<h2>Unhandled exception</h2><table cellpadding=\"4\">")
            .Append(Row("Ticket", alert.Ticket))
            .Append(Row("Correlation", alert.CorrelationId))
            .Append(Row("When (UTC)", alert.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)))
            .Append(Row("Environment", alert.Environment))
            .Append(Row("Request", $"{alert.Method} {alert.Path}{alert.Query}"))
            .Append(Row("User", alert.User))
            .Append(Row("Tenant", alert.Tenant))
            .Append(Row("Type", alert.ExceptionType))
            .Append(Row("Message", alert.Message))
            .Append("</table><pre>").Append(WebUtility.HtmlEncode(alert.Details)).Append("</pre>")
            .ToString();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to record error {Ticket} in the error log")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception, string ticket);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to send error alert for ticket {Ticket}")]
    private static partial void LogSendFailed(ILogger logger, Exception exception, string ticket);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Alert for ticket {Ticket} throttled (fingerprint {Fingerprint})")]
    private static partial void LogThrottled(ILogger logger, string ticket, string fingerprint);
}
