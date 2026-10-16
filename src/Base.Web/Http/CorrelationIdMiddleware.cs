using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Base.Web.Http;

/// <summary>
/// Accepts a caller's <c>X-Correlation-ID</c> (if well-formed) or uses the trace id, echoes it on the response and adds
/// it to the logging scope so every log line of the request carries it.
/// </summary>
internal sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = SafeId().IsMatch(incoming) ? incoming : Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        context.Items[HeaderName] = correlationId;
        Activity.Current?.SetTag("correlation.id", correlationId);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal) { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    public static string Get(HttpContext context) =>
        context.Items[HeaderName] as string ?? Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

    [GeneratedRegex("^[A-Za-z0-9._-]{8,64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SafeId();
}
