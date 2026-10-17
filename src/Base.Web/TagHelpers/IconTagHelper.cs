using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Base.Web.TagHelpers;

/// <summary><c>&lt;icon name="users" /&gt;</c>: a Lucide icon from the sprite at <c>/img/icons.svg</c>, hidden from screen readers.</summary>
[HtmlTargetElement("icon", TagStructure = TagStructure.WithoutEndTag)]
public sealed class IconTagHelper(IUrlHelperFactory urls) : TagHelper
{
    public string Name { get; set; } = string.Empty;

    [HtmlAttributeName("class")]
    public string? CssClass { get; set; }

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var sprite = urls.GetUrlHelper(ViewContext).Content("~/img/icons.svg");
        output.TagName = "svg";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", string.IsNullOrWhiteSpace(CssClass) ? "icon" : $"icon {CssClass}");
        output.Attributes.SetAttribute("aria-hidden", "true");
        output.Attributes.SetAttribute("focusable", "false");
        output.Content.SetHtmlContent($"<use href=\"{sprite}#{System.Net.WebUtility.HtmlEncode(Name)}\"></use>");
    }
}
