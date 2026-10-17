using Base.Application.Releases;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Base.Web.Releases;

/// <summary>
/// Gates a controller or action on a release feature: <c>[RequiresRelease(ReleaseFeatureCodes.X)]</c>. Pages explain
/// how to switch it on (What's new); API calls get 404, as if the endpoint weren't there yet for this tenant.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequiresReleaseAttribute(string feature) : Attribute, IAsyncAuthorizationFilter
{
    public string Feature { get; } = feature;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var services = context.HttpContext.RequestServices;
        var definition = services.GetRequiredService<IReleaseCatalog>().Find(Feature)
            ?? throw new InvalidOperationException($"[RequiresRelease(\"{Feature}\")] is not a feature in the ReleaseCatalog.");
        if (await services.GetRequiredService<ITenantReleases>().IsOnAsync(Feature, context.HttpContext.RequestAborted))
        {
            return;
        }

        if (context.ActionDescriptor.EndpointMetadata.OfType<ApiControllerAttribute>().Any())
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Not available yet",
                Detail = $"{definition.Name} isn't switched on for your organisation.",
            })
            { StatusCode = StatusCodes.Status404NotFound };
            return;
        }

        context.Result = new ViewResult
        {
            ViewName = "ReleaseUnavailable",
            StatusCode = StatusCodes.Status404NotFound,
            ViewData = new ViewDataDictionary<ReleaseFeature>(services.GetRequiredService<IModelMetadataProvider>(), context.ModelState) { Model = definition },
        };
    }
}
