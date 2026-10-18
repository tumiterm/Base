using Base.Application.Flags;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Base.Web.TagHelpers;

/// <summary>
/// Shows an element only when a feature flag is on for the organisation: <c>&lt;a asp-flag="@FeatureFlagCodes.X"&gt;</c>.
/// Cosmetic; the endpoint must still carry <c>[RequiresFlag]</c> or check <c>IFeatureFlags</c>.
/// </summary>
[HtmlTargetElement(Attributes = AttributeName)]
public sealed class FlagTagHelper(IFeatureFlags flags, IFeatureFlagCatalog catalog) : TagHelper
{
    private const string AttributeName = "asp-flag";

    [HtmlAttributeName(AttributeName)]
    public string Flag { get; set; } = string.Empty;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (catalog.Find(Flag) is null)
        {
            throw new InvalidOperationException($"asp-flag=\"{Flag}\" is not a flag in the FeatureFlagCatalog.");
        }

        if (!await flags.IsOnAsync(Flag))
        {
            output.SuppressOutput();
        }
    }
}
