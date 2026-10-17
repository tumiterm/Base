using Base.Application.Subscriptions;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Base.Web.TagHelpers;

/// <summary>
/// Hides an element when the tenant's plan leaves the feature out: <c>&lt;a asp-feature="@FeatureCodes.Documents"&gt;</c>.
/// Cosmetic, like <c>asp-permission</c>; the endpoint must still carry <c>[RequiresFeature]</c>.
/// </summary>
[HtmlTargetElement(Attributes = AttributeName)]
public sealed class FeatureTagHelper(ITenantEntitlements entitlements) : TagHelper
{
    private const string AttributeName = "asp-feature";

    [HtmlAttributeName(AttributeName)]
    public string Feature { get; set; } = string.Empty;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (EntitlementCatalog.Find(Feature) is not { Kind: EntitlementKind.Feature })
        {
            throw new InvalidOperationException($"asp-feature=\"{Feature}\" is not a feature in the EntitlementCatalog.");
        }

        if (!await entitlements.HasFeatureAsync(Feature))
        {
            output.SuppressOutput();
        }
    }
}
