using System.Net;
using System.Text.RegularExpressions;

namespace Base.Application.Common.Text;

/// <summary>
/// Fills <c>{{ Variable }}</c> placeholders. Deliberately tiny: no logic in templates. Values are HTML-encoded in HTML
/// parts. An unknown variable is an error, so a typo never silently sends "Dear ,".
/// </summary>
public static partial class TemplateRenderer
{
    public static string Render(string template, IReadOnlyDictionary<string, string?> values, bool html)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        return Placeholder().Replace(template, match =>
        {
            var name = match.Groups["name"].Value;
            if (!values.TryGetValue(name, out var value))
            {
                throw new InvalidOperationException($"Template uses '{{{{{name}}}}}', which was not supplied.");
            }

            return html ? WebUtility.HtmlEncode(value ?? string.Empty) : value ?? string.Empty;
        });
    }

    /// <summary>The variable names a template uses.</summary>
    public static IReadOnlySet<string> VariablesIn(string template) =>
        Placeholder().Matches(template).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\{\{\s*(?<name>[A-Za-z][A-Za-z0-9_]*)\s*\}\}", RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Placeholder();
}
