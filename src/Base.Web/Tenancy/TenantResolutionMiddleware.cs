using Base.Application.Common.Abstractions;
using Base.Infrastructure.Tenancy;
using Microsoft.Extensions.Options;

namespace Base.Web.Tenancy;

/// <summary>
/// Resolves the tenant before authentication. Single-tenant: always the default tenant. Multi-tenant: the request's
/// host must be a verified tenant domain or one of <c>DefaultTenantHosts</c>; unknown hosts get 404 unless fallback is
/// enabled (logged loudly).
/// </summary>
internal sealed partial class TenantResolutionMiddleware(RequestDelegate next, IOptions<TenancyOptions> options, IHostEnvironment environment, ILogger<TenantResolutionMiddleware> logger)
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
            var host = context.Request.Host.Host;
            tenant = settings.DefaultTenantHosts.Contains(host, StringComparer.OrdinalIgnoreCase)
                ? await resolver.GetDefaultAsync(context.RequestAborted)
                : await resolver.ResolveByHostAsync(host, context.RequestAborted);
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
            if (tenant is null && environment.IsDevelopment())
            {
                // Production says nothing about which hosts exist; on a developer's machine, say how to fix it.
                await context.Response.WriteAsync(
                    $"No organisation uses {context.Request.Host.Host}. Add it under Platform > Tenants > (the organisation) > Domains and mark it verified, " +
                    "or open localhost for the platform. Names ending in .dev.localhost reach this computer and are covered by the development certificate.",
                    context.RequestAborted);
            }

            return;
        }

        tenantContext.SetTenant(tenant.Id, tenant.Code);
        await next(context);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unknown host {Host}; serving the default tenant because Tenancy:FallbackToDefaultTenant is on")]
    private static partial void LogFallback(ILogger logger, string host);
}
