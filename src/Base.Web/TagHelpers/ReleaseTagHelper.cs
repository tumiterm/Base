using Base.Application.Releases;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Base.Web.TagHelpers;

/// <summary>
/// Shows an element only when a release feature is on for the tenant: <c>&lt;a asp-release="@ReleaseFeatureCodes.X"&gt;</c>.
/// Cosmetic; the endpoint must still carry <c>[RequiresRelease]</c> or check <c>ITenantReleases</c>.
/// </summary>
[HtmlTargetElement(Attributes = AttributeName)]
public sealed class ReleaseTagHelper(ITenantReleases releases, IReleaseCatalog catalog) : TagHelper
{
    private const string AttributeName = "asp-release";

    [HtmlAttributeName(AttributeName)]
    public string Feature { get; set; } = string.Empty;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (catalog.Find(Feature) is null)
        {
            throw new InvalidOperationException($"asp-release=\"{Feature}\" is not a feature in the ReleaseCatalog.");
        }

        if (!await releases.IsOnAsync(Feature))
        {
            output.SuppressOutput();
        }
    }
}
