using System.Collections.Frozen;

namespace Base.Application.Documents.Generation;

/// <summary>The embedded <c>Documents/Templates/{Code}.html</c> files, read once.</summary>
internal static class DocumentTemplateFiles
{
    private const string Prefix = "Documents.";

    private static readonly Lazy<FrozenDictionary<string, string>> Files = new(Load);

    public static IReadOnlyCollection<string> Codes => Files.Value.Keys;

    public static string? Find(string code) => Files.Value.GetValueOrDefault(code);

    private static FrozenDictionary<string, string> Load()
    {
        var assembly = typeof(DocumentTemplateFiles).Assembly;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".html", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            result[resource[Prefix.Length..^".html".Length]] = reader.ReadToEnd();
        }

        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
