using System.Collections.Frozen;

namespace Base.Application.Common.Text;

/// <summary>
/// Normalises "nothing" answers on optional free-text fields ("N/A", "none", "-") to null at save time,
/// so reports and uniqueness checks aren't polluted by placeholder values.
/// </summary>
public static class PlaceholderText
{
    private static readonly FrozenSet<string> Placeholders = new[]
    {
        "n/a", "na", "n.a", "n.a.", "none", "nil", "null", "nothing", "not applicable", "-", "--", "---", ".", "0", "x", "tbc", "tba",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the trimmed value, or null when it is blank or a known placeholder.</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return Placeholders.Contains(trimmed) ? null : trimmed;
    }

    public static bool IsPlaceholder(string? value) => value is not null && Clean(value) is null;
}
