using Base.Application.Payments;

namespace Base.Web.Http;

/// <summary>
/// Baseline browser security headers. The CSP allows only same-origin scripts and styles (no inline script); forms may
/// post only here and to the configured payment gateways' checkouts.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next, PaymentCheckoutOrigins checkoutOrigins)
{
    private readonly string _contentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; " +
        "connect-src 'self'; frame-ancestors 'none'; form-action " + string.Join(' ', ["'self'", .. checkoutOrigins.Origins]) +
        "; base-uri 'self'; object-src 'none'";

    // A file previewed inside one of our pages: framed by us only, and (except PDFs, which browsers won't render in
    // a sandbox) in a sandbox with no scripts, forms or same-origin access.
    private const string SandboxedPreviewPolicy =
        "sandbox; default-src 'none'; img-src 'self' data:; media-src 'self'; style-src 'unsafe-inline'; frame-ancestors 'self'";

    private const string PdfPreviewPolicy = "default-src 'none'; frame-ancestors 'self'";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";

            if (context.Features.Get<FramedPreview>() is { } preview)
            {
                headers.XFrameOptions = "SAMEORIGIN";
                headers.ContentSecurityPolicy = preview.Sandboxed ? SandboxedPreviewPolicy : PdfPreviewPolicy;
                headers["Cross-Origin-Resource-Policy"] = "same-origin";
                return Task.CompletedTask;
            }

            headers.XFrameOptions = "DENY";

            // The API reference UI (development only) loads its own assets; everything else gets the strict policy.
            if (!context.Request.Path.StartsWithSegments("/scalar", StringComparison.OrdinalIgnoreCase))
            {
                headers.ContentSecurityPolicy = _contentSecurityPolicy;
            }

            return Task.CompletedTask;
        });

        return next(context);
    }
}

/// <summary>Marks a response as a file preview that our own pages may show in a frame.</summary>
internal sealed record FramedPreview(bool Sandboxed);

internal static class FramedPreviewExtensions
{
    /// <summary>Lets this response be framed by same-origin pages, sandboxed unless it is a PDF.</summary>
    public static void AllowFramedPreview(this HttpResponse response, bool sandboxed)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.HttpContext.Features.Set(new FramedPreview(sandboxed));
    }
}
