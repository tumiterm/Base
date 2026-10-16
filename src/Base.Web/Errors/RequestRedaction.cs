using System.Text.RegularExpressions;

namespace Base.Web.Errors;

/// <summary>Masks sensitive query-string values before they reach logs or alert emails.</summary>
internal static partial class RequestRedaction
{
    public const string Mask = "***";

    public static string RedactQuery(QueryString query)
    {
        if (!query.HasValue)
        {
            return string.Empty;
        }

        var pairs = query.Value!.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair =>
        {
            var index = pair.IndexOf('=', StringComparison.Ordinal);
            var key = index < 0 ? pair : pair[..index];
            return index >= 0 && IsSensitive(Uri.UnescapeDataString(key)) ? $"{key}={Mask}" : pair;
        });

        return "?" + string.Join('&', pairs);
    }

    public static bool IsSensitive(string key) => SensitiveKey().IsMatch(key);

    [GeneratedRegex("pass|pwd|secret|token|key|code|otp|pin|auth|signature|sig|idnumber|ssn",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SensitiveKey();
}
