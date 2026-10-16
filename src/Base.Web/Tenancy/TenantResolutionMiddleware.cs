using Base.Application.Common.Abstractions;
using Base.Infrastructure.Tenancy;
using Microsoft.Extensions.Options;

namespace Base.Web.Tenancy;

/// <summary>
/// Resolves the tenant before authentication. Single-tenant: always the default tenant. Multi-tenant: the request's
/// host must be a verified tenant domain; unknown hosts get 404 unless fallback is enabled (logged loudly).
/// </summary>
internal sealed partial class TenantResolutionMiddleware(RequestDelegate next, IOptions<TenancyOptions> options, ILogger<TenantResolutionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ITenantResolver resolver, ITenantContext tenantContext)
    {
        if (context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var settings = options.Value;
        ResolvedTenant? tenant;
        if (settings.MultiTenant)
        {
            tenant = await resolver.ResolveByHostAsync(context.Request.Host.Host, context.RequestAborted);
            if (tenant is null && settings.FallbackToDefaultTenant)
            {
                LogFallback(logger, context.Request.Host.Host);
                tenant = await resolver.GetDefaultAsync(context.RequestAborted);
            }
        }
        else
        {
            tenant = await resolver.GetDefaultAsync(context.RequestAborted);
        }

        if (tenant is null || !tenant.IsActive)
        {
            context.Response.StatusCode = tenant is null ? StatusCodes.Status404NotFound : StatusCodes.Status403Forbidden;
            return;
        }

        tenantContext.SetTenant(tenant.Id, tenant.Code);
        await next(context);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unknown host {Host}; serving the default tenant because Tenancy:FallbackToDefaultTenant is on")]
    private static partial void LogFallback(ILogger logger, string host);
}
