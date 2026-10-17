using System.Reflection;

namespace Base.Application.Operations;

/// <summary>
/// The running build: this product's version (with the commit, when built from git), and the Base version it is
/// built on. Both come from Directory.Build.props.
/// </summary>
public static class AppVersion
{
    private static readonly Assembly Assembly = typeof(AppVersion).Assembly;

    /// <summary>"1.4.0+3f2c1ab..." when the build knew its commit, else "1.4.0".</summary>
    public static string Informational { get; } =
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>"1.4.0": the product version without build metadata.</summary>
    public static string Version { get; } = Informational.Split('+', 2)[0];

    /// <summary>The first 7 characters of the commit, or null.</summary>
    public static string? Commit { get; } = Informational.Split('+', 2) is [_, var sha] && sha.Length > 0 ? sha[..Math.Min(7, sha.Length)] : null;

    /// <summary>The Base starter version this code was created from (or is, in the Base repository).</summary>
    public static string BaseVersion { get; } =
        Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => string.Equals(a.Key, "BaseVersion", StringComparison.Ordinal))?.Value ?? Version;

    /// <summary>"1.4.0 (3f2c1ab) · Base 1.0.0", or "1.0.0 (3f2c1ab)" when they are the same.</summary>
    public static string Describe() =>
        (Commit is null ? Version : $"{Version} ({Commit})") + (string.Equals(Version, BaseVersion, StringComparison.Ordinal) ? "" : $" · Base {BaseVersion}");
}
