using Base.Application.Branding;
using Microsoft.Net.Http.Headers;

namespace Base.Web.Theming;

/// <summary>
/// <c>/theme.css</c> (the tenant's accent as CSS variables) and <c>/branding/logo</c>. Both are public (the sign-in page
/// uses them) and cached hard: the layout links them with the branding version, so a change is a new URL.
/// </summary>
internal static class BrandingEndpoints
{
    public static IEndpointRouteBuilder MapBranding(this IEndpointRouteBuilder app)
    {
        app.MapGet("/theme.css", async (HttpContext context, IBrandingService branding, CancellationToken cancellationToken) =>
        {
            var theme = await branding.GetAsync(cancellationToken);
            Cache(context, theme.Version);
            return Microsoft.AspNetCore.Http.Results.Text(ThemePalette.From(theme.PrimaryColor).Css(), "text/css; charset=utf-8");
        }).AllowAnonymous();

        app.MapGet("/branding/logo", async (HttpContext context, IBrandingService branding, CancellationToken cancellationToken) =>
        {
            var logo = await branding.OpenLogoAsync(cancellationToken);
            if (logo is null)
            {
                return Microsoft.AspNetCore.Http.Results.NotFound();
            }

            Cache(context, (await branding.GetAsync(cancellationToken)).Version);
            return Microsoft.AspNetCore.Http.Results.Stream(logo.Content, logo.ContentType);
        }).AllowAnonymous();

        return app;
    }

    private static void Cache(HttpContext context, string version)
    {
        // Versioned URL: cache for a year. Without the version (or a stale one), revalidate.
        var requested = context.Request.Query["v"].ToString();
        context.Response.Headers[HeaderNames.CacheControl] = string.Equals(requested, version, StringComparison.Ordinal)
            ? "public, max-age=31536000, immutable"
            : "no-cache";
        context.Response.Headers[HeaderNames.Vary] = "Host";
    }
}
