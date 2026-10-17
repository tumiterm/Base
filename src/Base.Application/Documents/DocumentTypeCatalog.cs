using System.Collections.Frozen;
using Base.Application.Files;

namespace Base.Application.Documents;

/// <summary>Document type codes. Adding a type = a constant here + a <see cref="DocumentTypeCatalog"/> entry.</summary>
public static class DocumentTypes
{
    public const string General = "general";
    public const string Letter = "letter";
}

/// <summary>
/// What a kind of document allows and how long it is kept. The code is also the file category of its versions, so
/// <c>Files.CategoryProviders</c> can send a type to a particular storage provider.
/// </summary>
/// <param name="AllowedExtensions">Narrows <c>Files.AllowedExtensions</c> for uploads of this type; null allows all of them.</param>
/// <param name="ArchiveAfterDays">Final documents are archived this long after they were finalised; null never.</param>
/// <param name="DeleteAfterDays">Archived documents are deleted this long after they were archived; null never.</param>
public sealed record DocumentTypeDefinition(
    string Code,
    string Name,
    string Description,
    IReadOnlyList<string>? AllowedExtensions = null,
    int? ArchiveAfterDays = null,
    int? DeleteAfterDays = null);

/// <summary>The single source of truth for document types (tests check codes are valid file categories).</summary>
public static class DocumentTypeCatalog
{
    public static IReadOnlyList<DocumentTypeDefinition> All { get; } =
    [
        new(DocumentTypes.General, "General", "Any document. Kept until someone deletes it."),
        new(DocumentTypes.Letter, "Letter", "Letters produced from templates or uploaded as PDF.", AllowedExtensions: [".pdf"],
            ArchiveAfterDays: 365, DeleteAfterDays: 365 * 5),
    ];

    private static readonly FrozenDictionary<string, DocumentTypeDefinition> ByCode =
        All.ToFrozenDictionary(t => t.Code, StringComparer.Ordinal);

    public static DocumentTypeDefinition? Find(string? code) => code is null ? null : ByCode.GetValueOrDefault(code);

    public static bool IsValidCode(string code) => ObjectKeys.IsValidCategory(code) && code.Length <= Domain.Documents.Document.TypeMaxLength;
}
