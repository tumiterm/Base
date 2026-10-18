using Base.Application.Flags;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Base.Web.Flags;

/// <summary>
/// Gates a controller or action on a feature flag: <c>[RequiresFlag(FeatureFlagCodes.X)]</c>. While it is off the
/// endpoint behaves as if it weren't there (404), for pages and API calls alike.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequiresFlagAttribute(string flag) : Attribute, IAsyncAuthorizationFilter
{
    public string Flag { get; } = flag;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var services = context.HttpContext.RequestServices;
        if (services.GetRequiredService<IFeatureFlagCatalog>().Find(Flag) is null)
        {
            throw new InvalidOperationException($"[RequiresFlag(\"{Flag}\")] is not a flag in the FeatureFlagCatalog.");
        }

        if (await services.GetRequiredService<IFeatureFlags>().IsOnAsync(Flag, context.HttpContext.RequestAborted))
        {
            return;
        }

        context.Result = context.ActionDescriptor.EndpointMetadata.OfType<ApiControllerAttribute>().Any()
            ? new ObjectResult(new ProblemDetails { Status = StatusCodes.Status404NotFound, Title = "Not found" }) { StatusCode = StatusCodes.Status404NotFound }
            : new NotFoundResult();
    }
}
