using System.Globalization;

namespace Base.Application.Releases;

/// <summary>Compares SemVer strings by major.minor.patch (pre-release and build suffixes are ignored).</summary>
public static class ReleaseVersions
{
    public static Version Parse(string? version)
    {
        var core = (version ?? string.Empty).Split('+', 2)[0].Split('-', 2)[0].Trim();
        var parts = core.Split('.');
        int Part(int i) => i < parts.Length && int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return new Version(Part(0), Part(1), Part(2));
    }

    public static int Compare(string? left, string? right) => Parse(left).CompareTo(Parse(right));

    /// <summary>"1.4.0" → "1.4", "1.4.2" → "1.4.2": how versions are shown.</summary>
    public static string Display(string? version)
    {
        var v = Parse(version);
        return v.Build == 0 ? $"{v.Major}.{v.Minor}" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>For screens: <see cref="Display"/>, or "None yet" for a tenant that never took a release.</summary>
    public static string Label(string? version) => Parse(version) == new Version(0, 0, 0) ? "None yet" : Display(version);
}
