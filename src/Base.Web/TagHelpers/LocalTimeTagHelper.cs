using System.Globalization;
using Base.Application.Settings;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Base.Web.TagHelpers;

/// <summary>
/// Renders a stored UTC timestamp in the tenant's display time zone:
/// <c>&lt;local-time value="@file.DateCreated" /&gt;</c> → <c>&lt;time datetime="…Z"&gt;16 Oct 2026 22:15&lt;/time&gt;</c>.
/// </summary>
[HtmlTargetElement("local-time", TagStructure = TagStructure.WithoutEndTag)]
public sealed class LocalTimeTagHelper(IApplicationSettingService settings) : TagHelper
{
    public DateTimeOffset? Value { get; set; }

    public string Format { get; set; } = "dd MMM yyyy HH:mm";

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.TagName = "time";
        output.TagMode = TagMode.StartTagAndEndTag;
        if (Value is not { } value)
        {
            output.SuppressOutput();
            return;
        }

        var zoneId = await settings.GetAsync(SettingCodes.General.DisplayTimeZone);
        var zone = zoneId is not null && TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var found) ? found : TimeZoneInfo.Utc;
        output.Attributes.SetAttribute("datetime", value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        output.Content.SetContent(TimeZoneInfo.ConvertTime(value, zone).ToString(Format, CultureInfo.CurrentCulture));
    }
}
