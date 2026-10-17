using Base.Application.Common.Persistence;
using Base.Application.Common.Records;
using Base.Application.Documents;
using Base.Application.Documents.Generation;
using Base.Domain.Documents;

namespace Base.Web.ViewModels;

public sealed class DocumentFilterViewModel
{
    public string? Search { get; set; }

    public string? Type { get; set; }

    public string? Tag { get; set; }

    public DocumentStatus? Status { get; set; }

    public string? LinkType { get; set; }

    public string? LinkId { get; set; }

    public bool IncludeArchived { get; set; }

    public int Page { get; set; } = 1;

    public DocumentQuery ToQuery() => new(
        Search, Type, Tag, Status,
        string.IsNullOrWhiteSpace(LinkType) || string.IsNullOrWhiteSpace(LinkId) ? null : new DocumentLink(LinkType, LinkId),
        IncludeArchived, Page, 25);
}

public sealed record DocumentListViewModel(
    PagedResult<DocumentSummary> Documents,
    DocumentFilterViewModel Filter,
    bool CanCreate,
    IReadOnlyDictionary<RecordRef, ResolvedRecord> Links,
    bool PreviewEnabled = true)
{
    public ResolvedRecord? LinkOf(DocumentSummary document) =>
        document?.LinkedTo is { } link && Links.TryGetValue(link, out var resolved) ? resolved : null;
}

/// <summary>One document's page; <see cref="Viewing"/> is the version shown in the preview.</summary>
public sealed record DocumentPageViewModel(DocumentDetails Document, ResolvedRecord? LinkedTo, DocumentVersionView? Viewing, bool PreviewEnabled = true)
{
    public bool IsLatest => Viewing is null || Viewing.Number == Document.CurrentVersion;
}

/// <summary>Documents attached to one record, for the <c>LinkedDocuments</c> view component.</summary>
public sealed record LinkedDocumentsViewModel(string EntityType, string EntityId, IReadOnlyList<DocumentSummary> Documents, bool CanCreate, bool PreviewEnabled = true);

/// <summary>The upload drawer: <see cref="Id"/> keeps several on one page apart.</summary>
public sealed record UploadDrawerViewModel(string Id, string? LinkType = null, string? LinkId = null, string? ReturnUrl = null, string? LinkedName = null);

public sealed record GenerateDocumentViewModel(
    IReadOnlyList<DocumentTemplateDefinition> Templates,
    IReadOnlyCollection<string> Formats,
    DocumentTemplateDefinition? Selected,
    IReadOnlyDictionary<string, string?> Values,
    string? Title,
    string Format,
    bool Finalize);

/// <summary>What <c>_FileViewer</c> needs to show one file in the page.</summary>
public sealed record FileViewerViewModel(string PreviewUrl, string DownloadUrl, string FileName, string ContentType, long SizeBytes, bool PreviewEnabled = true);
