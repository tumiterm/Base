using Base.Application.Common.Persistence;
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

public sealed record DocumentListViewModel(PagedResult<DocumentSummary> Documents, DocumentFilterViewModel Filter, bool CanCreate);

/// <summary>Documents attached to one record, for the <c>LinkedDocuments</c> view component.</summary>
public sealed record LinkedDocumentsViewModel(string EntityType, string EntityId, IReadOnlyList<DocumentSummary> Documents, bool CanCreate);

public sealed record GenerateDocumentViewModel(
    IReadOnlyList<DocumentTemplateDefinition> Templates,
    IReadOnlyCollection<string> Formats,
    DocumentTemplateDefinition? Selected,
    IReadOnlyDictionary<string, string?> Values,
    string? Title,
    string Format,
    bool Finalize);
