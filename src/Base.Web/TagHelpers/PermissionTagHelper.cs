using Base.Application.Common.Abstractions;
using Base.Application.Security;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Base.Web.TagHelpers;

/// <summary>
/// Hides an element the user may not use: <c>&lt;a asp-permission="@PermissionCodes.Settings.View" ...&gt;</c>.
/// Hiding is cosmetic; the endpoint must still carry <c>[HasPermission]</c>.
/// </summary>
[HtmlTargetElement(Attributes = AttributeName)]
public sealed class PermissionTagHelper(ICurrentUser currentUser) : TagHelper
{
    private const string AttributeName = "asp-permission";

    [HtmlAttributeName(AttributeName)]
    public string Permission { get; set; } = string.Empty;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!PermissionCatalog.Exists(Permission))
        {
            throw new InvalidOperationException($"asp-permission=\"{Permission}\" is not in the PermissionCatalog.");
        }

        if (!currentUser.HasPermission(Permission))
        {
            output.SuppressOutput();
        }
    }
}
