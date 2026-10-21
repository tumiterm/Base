using Base.Application.Subscriptions;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Subscriptions;

/// <summary>
/// Keeps a suspended, cancelled or expired tenant's signed-in users out of the application: pages redirect to the
/// subscription page (which says why), API calls get 402. Signing in and out, and static files, stay open.
/// </summary>
internal sealed class SubscriptionGateMiddleware(RequestDelegate next)
{
    private static readonly string[] OpenPaths = ["/subscription", "/payments", "/account", "/error", "/health", "/theme.css", "/branding", "/css", "/js", "/lib", "/img", "/favicon.ico"];

    public async Task InvokeAsync(HttpContext context, ITenantEntitlements entitlements)
    {
        if (context.User.Identity?.IsAuthenticated != true || OpenPaths.Any(p => context.Request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        var access = await entitlements.GetAsync(context.RequestAborted);
        if (access.HasAccess)
        {
            await next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
            await context.Response.WriteAsJsonAsync(
                new ProblemDetails
                {
                    Status = StatusCodes.Status402PaymentRequired,
                    Title = "Subscription inactive",
                    Detail = $"This organisation's subscription is {SubscriptionStatusText.Describe(access.Status!.Value).ToLowerInvariant()}.",
                },
                options: null,
                contentType: "application/problem+json",
                cancellationToken: context.RequestAborted);
            return;
        }

        context.Response.Redirect("/subscription");
    }
}
