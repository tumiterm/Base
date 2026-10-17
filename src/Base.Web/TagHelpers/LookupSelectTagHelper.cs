using System.Text.Encodings.Web;
using Base.Application.MasterData;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Base.Web.TagHelpers;

/// <summary>
/// Fills a select with a lookup list: <c>&lt;select asp-for="DepartmentCode" asp-lookup="@LookupLists.People.Department"
/// asp-lookup-value="@Model.DepartmentCode" asp-lookup-empty="No department" data-combobox&gt;</c>. Active entries only,
/// plus the current value if it has since been hidden, so editing a record never silently drops it.
/// </summary>
[HtmlTargetElement("select", Attributes = ListAttribute)]
public sealed class LookupSelectTagHelper(ILookupService lookups) : TagHelper
{
    private const string ListAttribute = "asp-lookup";

    /// <summary>After the built-in select tag helper, so its attributes are in place.</summary>
    public override int Order => 10;

    [HtmlAttributeName(ListAttribute)]
    public string List { get; set; } = string.Empty;

    [HtmlAttributeName("asp-lookup-value")]
    public string? Value { get; set; }

    /// <summary>Text of a blank first option; omit to require a choice.</summary>
    [HtmlAttributeName("asp-lookup-empty")]
    public string? Empty { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (LookupCatalog.Find(List) is null)
        {
            throw new InvalidOperationException($"asp-lookup=\"{List}\" is not in the LookupCatalog.");
        }

        var values = await lookups.GetAsync(List, includeInactive: true);
        var html = HtmlEncoder.Default;
        if (Empty is not null)
        {
            output.Content.AppendHtml($"<option value=\"\">{html.Encode(Empty)}</option>");
        }

        foreach (var value in values.Where(v => v.IsActive || string.Equals(v.Code, Value, StringComparison.Ordinal)))
        {
            var selected = string.Equals(value.Code, Value, StringComparison.Ordinal) ? " selected" : string.Empty;
            var label = value.IsActive ? value.Name : value.Name + " (hidden)";
            output.Content.AppendHtml($"<option value=\"{html.Encode(value.Code)}\"{selected}>{html.Encode(label)}</option>");
        }
    }
}
