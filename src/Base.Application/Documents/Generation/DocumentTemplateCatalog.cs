using System.Collections.Frozen;

namespace Base.Application.Documents.Generation;

/// <summary>Output formats. A format is available when an <see cref="IDocumentRenderer"/> for it is registered.</summary>
public static class DocumentFormats
{
    /// <summary>Built in: the filled-in template itself.</summary>
    public const string Html = "html";

    /// <summary>Needs a PDF renderer: Gotenberg (configure <c>Documents:Pdf:Gotenberg</c>) or your own.</summary>
    public const string Pdf = "pdf";
}

/// <summary>Template codes. Adding a template = a constant here + a catalogue entry + <c>Documents/Templates/{Code}.html</c>.</summary>
public static class DocumentTemplates
{
    public const string Letter = "General.Letter";
}

/// <summary>
/// A document the application can produce. The HTML file uses <c>{{ Variable }}</c> placeholders (values are
/// HTML-encoded); <see cref="Variables"/> must be supplied by the caller, the built-ins are filled in automatically.
/// </summary>
public sealed record DocumentTemplateDefinition(string Code, string Name, string Description, string DocumentType, IReadOnlyList<string> Variables);

public static class DocumentTemplateCatalog
{
    public const string AppNameVariable = "AppName";

    /// <summary>Today's date in the <c>General.DisplayTimeZone</c>, e.g. "17 October 2026".</summary>
    public const string TodayVariable = "Today";

    public static IReadOnlyList<string> BuiltInVariables { get; } = [AppNameVariable, TodayVariable];

    public static IReadOnlyList<DocumentTemplateDefinition> All { get; } =
    [
        new(DocumentTemplates.Letter, "Letter", "A one-page letter with a subject and body.", DocumentTypes.Letter, ["RecipientName", "Subject", "Body"]),
    ];

    private static readonly FrozenDictionary<string, DocumentTemplateDefinition> ByCode =
        All.ToFrozenDictionary(t => t.Code, StringComparer.Ordinal);

    public static DocumentTemplateDefinition? Find(string? code) => code is null ? null : ByCode.GetValueOrDefault(code);
}
