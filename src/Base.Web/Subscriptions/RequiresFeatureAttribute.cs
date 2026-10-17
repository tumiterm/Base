using Base.Application.Subscriptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Base.Web.Subscriptions;

/// <summary>
/// Gates a controller or action on a plan feature: <c>[RequiresFeature(FeatureCodes.Documents)]</c>. Pages show
/// "not in your plan"; API calls get 403 ProblemDetails. Tenants without a subscription have every feature.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequiresFeatureAttribute : Attribute, IAsyncAuthorizationFilter
{
    public RequiresFeatureAttribute(string feature)
    {
        Definition = EntitlementCatalog.Find(feature) is { Kind: EntitlementKind.Feature } definition
            ? definition
            : throw new InvalidOperationException($"[RequiresFeature(\"{feature}\")] is not a feature in the EntitlementCatalog.");
    }

    public string Feature => Definition.Code;

    private EntitlementDefinition Definition { get; }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var entitlements = context.HttpContext.RequestServices.GetRequiredService<ITenantEntitlements>();
        if (await entitlements.HasFeatureAsync(Feature, context.HttpContext.RequestAborted))
        {
            return;
        }

        var detail = $"{Definition.Name} isn't included in your organisation's plan.";
        if (context.ActionDescriptor.EndpointMetadata.OfType<ApiControllerAttribute>().Any())
        {
            context.Result = new ObjectResult(new ProblemDetails { Status = StatusCodes.Status403Forbidden, Title = "Not in your plan", Detail = detail })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
            return;
        }

        var metadata = context.HttpContext.RequestServices.GetRequiredService<IModelMetadataProvider>();
        context.Result = new ViewResult
        {
            ViewName = "FeatureUnavailable",
            StatusCode = StatusCodes.Status403Forbidden,
            ViewData = new ViewDataDictionary<EntitlementDefinition>(metadata, context.ModelState) { Model = Definition },
        };
    }
}
