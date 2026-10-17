using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Base.Application.Integrations;
using Base.Application.Security;
using Base.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Base.Web.Api;

/// <summary>
/// Authenticates API clients by key: <c>Authorization: Bearer bk_…</c> or <c>X-Api-Key: bk_…</c>, on <c>/api</c> only.
/// The principal carries the client's permissions (no user id), so <c>[HasPermission]</c>, auditing ("API: name")
/// and rate limiting work as they do for people. No cookie is involved, so no antiforgery token is needed.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyAuthenticator authenticator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    private const string FailureKey = "api-key-failure";

    /// <summary>The key from either header, or null. Used by the policy scheme to pick this handler.</summary>
    public static string? KeyFrom(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headers.TryGetValue(HeaderName, out var header) && header.Count == 1)
        {
            return header[0];
        }

        var authorization = request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer " + ApiKeys.Scheme + "_", StringComparison.Ordinal) ? authorization["Bearer ".Length..].Trim() : null;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || KeyFrom(Request) is not { } key)
        {
            return AuthenticateResult.NoResult();
        }

        var result = await authenticator.AuthenticateAsync(key, Context.RequestAborted);
        if (result.Client is not { } client)
        {
            Context.Items[FailureKey] = result.Failure;
            return AuthenticateResult.Fail(result.Failure == ApiKeyFailure.NotInPlan ? "API access isn't included in the plan." : "Invalid API key.");
        }

        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(AppClaimTypes.ApiClientId, client.Id.ToString()));
        identity.AddClaim(new Claim(AppClaimTypes.DisplayName, "API: " + client.Name));
        identity.AddClaim(new Claim(ClaimTypes.Name, client.Name));
        identity.AddClaim(new Claim(AppClaimTypes.TenantId, client.TenantId.ToString()));
        identity.AddClaim(new Claim(AppClaimTypes.RateLimit, client.RequestsPerMinute.ToString(CultureInfo.InvariantCulture)));
        foreach (var permission in PermissionCatalog.Expand(client.Permissions))
        {
            identity.AddClaim(new Claim(PermissionCatalog.ClaimType, permission));
        }

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var notInPlan = Context.Items.TryGetValue(FailureKey, out var failure) && failure is ApiKeyFailure.NotInPlan;
        Response.StatusCode = notInPlan ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        await Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = Response.StatusCode,
                Title = notInPlan ? "Not in your plan" : "Unauthorized",
                Detail = notInPlan ? "API access isn't included in this organisation's plan." : "The API key is missing, invalid, revoked or expired.",
            },
            options: null,
            contentType: "application/problem+json",
            cancellationToken: Context.RequestAborted);
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Response.WriteAsJsonAsync(
            new ProblemDetails { Status = 403, Title = "Forbidden", Detail = "This API client doesn't have the permission this call needs." },
            options: null,
            contentType: "application/problem+json",
            cancellationToken: Context.RequestAborted);
    }
}

/// <summary>Whether the request was authenticated by API key (then there is no cookie and no CSRF risk).</summary>
internal static class ApiCaller
{
    public static bool IsApiClient(this ClaimsPrincipal user) => user?.HasClaim(c => string.Equals(c.Type, AppClaimTypes.ApiClientId, StringComparison.Ordinal)) == true;

    /// <summary>A stable id for limits and idempotency: "client:{id}", "user:{id}", or "ip:{address}".</summary>
    public static string Of(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = context.User;
        if (user.FindFirst(AppClaimTypes.ApiClientId)?.Value is { } client)
        {
            return "client:" + client;
        }

        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } id && user.Identity?.IsAuthenticated == true
            ? "user:" + id
            : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }
}
