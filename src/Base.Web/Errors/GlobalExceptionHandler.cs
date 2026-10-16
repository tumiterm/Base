using Base.Application.Common.Abstractions;
using Base.Web.Http;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Errors;

/// <summary>
/// Last line of defence for unhandled exceptions. Logs once with a ticket number, queues a throttled alert, and
/// answers API callers with RFC 9457 ProblemDetails. For pages it returns false so the exception-handler middleware
/// re-executes <c>/error</c> (status 500 preserved, no redirect).
/// </summary>
internal sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetails,
    ErrorAlertQueue alerts,
    IHostEnvironment environment,
    TimeProvider time,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public const string TicketItemKey = "ErrorTicket";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            // The client went away; nothing to report.
            httpContext.Response.StatusCode = 499;
            return true;
        }

        var correlationId = CorrelationIdMiddleware.Get(httpContext);
        var ticket = "E-" + correlationId[..Math.Min(10, correlationId.Length)].ToUpperInvariant();
        httpContext.Items[TicketItemKey] = ticket;

        // By now the middleware has rewritten Request.Path to the error page; the feature keeps the original.
        var request = httpContext.Request;
        var path = httpContext.Features.Get<IExceptionHandlerFeature>()?.Path ?? request.Path.Value ?? string.Empty;
        var query = RequestRedaction.RedactQuery(request.QueryString);
        var currentUser = httpContext.RequestServices.GetService<ICurrentUser>();
        var tenant = httpContext.RequestServices.GetService<ITenantContext>();

        LogUnhandled(logger, exception, ticket, request.Method, path, query);

        alerts.TryEnqueue(new ErrorAlert(
            ticket,
            correlationId,
            ErrorAlert.FingerprintOf(exception),
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            exception.ToString(),
            request.Method,
            path,
            query,
            currentUser?.AuditName,
            tenant?.TenantCode,
            environment.EnvironmentName,
            time.GetUtcNow()));

        if (!IsApiRequest(path, request.Headers))
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Detail = $"The team has been notified. Quote reference {ticket} when contacting support.",
                Extensions = { ["ticket"] = ticket },
            },
        });
    }

    internal static bool IsApiRequest(HttpRequest request) => IsApiRequest(request.Path.Value ?? string.Empty, request.Headers);

    internal static bool IsApiRequest(string path, IHeaderDictionary headers) =>
        new PathString(path).StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || headers.Accept.Any(a => a?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception {Ticket} on {Method} {Path}{Query}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string ticket, string method, string path, string query);
}
