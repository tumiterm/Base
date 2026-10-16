using System.Text.RegularExpressions;

namespace Base.Application.Files;

/// <summary>Builds provider object keys: <c>{tenant}/{category}/{yyyy}/{MM}/{guid}{ext}</c>. Never the user's file name.</summary>
public static partial class ObjectKeys
{
    public static string Build(Guid tenantId, string category, DateTimeOffset utcNow, Guid fileId, string extension)
    {
        if (!IsValidCategory(category))
        {
            throw new ArgumentException("Category must be 1-50 lower-case letters, digits or dashes.", nameof(category));
        }

        var ext = extension.ToLowerInvariant();
        if (ext.Length > 0 && !SafeExtension().IsMatch(ext))
        {
            throw new ArgumentException("Extension must look like .pdf.", nameof(extension));
        }

        var utc = utcNow.ToUniversalTime();
        return $"{tenantId:N}/{category}/{utc:yyyy}/{utc:MM}/{fileId:N}{ext}";
    }

    public static bool IsValidCategory(string? category) => category is not null && SafeCategory().IsMatch(category);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,49}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SafeCategory();

    [GeneratedRegex(@"^\.[a-z0-9]{1,10}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SafeExtension();
}
