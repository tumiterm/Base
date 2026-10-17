using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Base.Application.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Api;

/// <summary>
/// Makes unsafe API calls safe to retry: a client sends <c>Idempotency-Key: &lt;unique value&gt;</c> with a POST, PUT,
/// PATCH or DELETE, and a repeat with the same key gets the first response (with <c>Idempotent-Replayed: true</c>)
/// instead of the work being done twice. The same key with a different request is refused (422); a repeat while the
/// first is still running gets 409. Server errors aren't remembered, so a retry runs again.
/// </summary>
internal sealed partial class IdempotencyMiddleware(RequestDelegate next)
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";
    private const int MaxRequestBytes = 1024 * 1024;
    private const int MaxStoredResponseBytes = 256 * 1024;

    public async Task InvokeAsync(HttpContext context, IIdempotencyStore store)
    {
        var request = context.Request;
        if (!request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method)
            || !request.Headers.TryGetValue(HeaderName, out var header)
            || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var key = header.ToString();
        if (!ValidKey().IsMatch(key))
        {
            await ProblemAsync(context, StatusCodes.Status400BadRequest, "Invalid Idempotency-Key", "Use 1–100 letters, digits, dashes, underscores, colons or dots, e.g. a UUID.");
            return;
        }

        if (request.ContentLength > MaxRequestBytes)
        {
            await ProblemAsync(context, StatusCodes.Status413PayloadTooLarge, "Too large to replay", "Requests with an Idempotency-Key can be at most 1 MB.");
            return;
        }

        request.EnableBuffering(MaxRequestBytes);
        var hash = await HashAsync(request, context.RequestAborted);
        var claim = await store.BeginAsync(ApiCaller.Of(context), key, hash, context.RequestAborted);
        switch (claim.Outcome)
        {
            case IdempotencyOutcome.Mismatch:
                await ProblemAsync(context, StatusCodes.Status422UnprocessableEntity, "Idempotency-Key reused", "This key was already used for a different request. Use a new key for a new request.");
                return;
            case IdempotencyOutcome.InProgress:
                context.Response.Headers.RetryAfter = "1";
                await ProblemAsync(context, StatusCodes.Status409Conflict, "Still processing", "The first request with this Idempotency-Key hasn't finished. Retry shortly.");
                return;
            case IdempotencyOutcome.Replay:
                context.Response.StatusCode = claim.StatusCode;
                context.Response.Headers[ReplayedHeader] = "true";
                if (claim.ContentType is not null)
                {
                    context.Response.ContentType = claim.ContentType;
                }

                if (claim.Body is { Length: > 0 } body)
                {
                    await context.Response.Body.WriteAsync(body, context.RequestAborted);
                }

                return;
        }

        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        var completed = false;
        try
        {
            await next(context);
            if (context.Response.StatusCode < 500 && buffer.Length <= MaxStoredResponseBytes)
            {
                await store.CompleteAsync(claim.RecordId, context.Response.StatusCode, context.Response.ContentType, buffer.ToArray(), CancellationToken.None);
                completed = true;
            }
        }
        finally
        {
            context.Response.Body = original;
            if (!completed)
            {
                await store.AbandonAsync(claim.RecordId, CancellationToken.None);
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(original, CancellationToken.None);
        }
    }

    private static async Task<string> HashAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{request.Method} {request.Path}{request.QueryString}\n"));
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            hash.AppendData(chunk, 0, read);
        }

        request.Body.Position = 0;
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static Task ProblemAsync(HttpContext context, int status, string title, string detail)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title, Detail = detail }, options: null, contentType: "application/problem+json", context.RequestAborted);
    }

    [GeneratedRegex("^[A-Za-z0-9_\\-:.]{1,100}$", RegexOptions.None, 1000)]
    private static partial Regex ValidKey();
}
