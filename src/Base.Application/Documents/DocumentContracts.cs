using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Files;
using Base.Domain.Documents;

namespace Base.Application.Documents;

/// <summary>The record a document belongs to, e.g. <c>("Invoice", "42")</c>. Any entity type, no foreign key.</summary>
public sealed record DocumentLink(string EntityType, string EntityId);

/// <summary>A file to store as a document version. The stream is read once and not disposed.</summary>
public sealed record DocumentUpload(Stream Content, string FileName);

public sealed record CreateDocumentRequest(
    string Title,
    string DocumentType,
    DocumentUpload File,
    string? Description = null,
    IReadOnlyList<string>? Tags = null,
    DocumentLink? LinkedTo = null,
    string? Comment = null);

public sealed record UpdateDocumentRequest(string Title, string? Description, IReadOnlyList<string>? Tags);

/// <summary>Archived documents are left out unless <see cref="IncludeArchived"/> or <see cref="Status"/> asks for them.</summary>
public sealed record DocumentQuery(
    string? Search = null,
    string? DocumentType = null,
    string? Tag = null,
    DocumentStatus? Status = null,
    DocumentLink? LinkedTo = null,
    bool IncludeArchived = false,
    int Page = 1,
    int PageSize = PageRequest.DefaultPageSize);

public sealed record DocumentSummary(
    Guid Id,
    string Title,
    string DocumentType,
    DocumentStatus Status,
    int CurrentVersion,
    IReadOnlyList<string> Tags,
    string? LinkedEntityType,
    string? LinkedEntityId,
    DateTimeOffset DateCreated,
    DateTimeOffset DateModified,
    string? UserCreated);

public sealed record DocumentVersionView(
    int Number,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DocumentSource Source,
    string? Comment,
    string? TemplateCode,
    DateTimeOffset CreatedAt,
    string? CreatedBy);

/// <summary>What the current user may do with the document, so screens only offer allowed actions.</summary>
public sealed record DocumentAbilities(bool Edit, bool Manage);

public sealed record DocumentDetails(
    Guid Id,
    string Title,
    string DocumentType,
    string? Description,
    DocumentStatus Status,
    int CurrentVersion,
    IReadOnlyList<string> Tags,
    string? LinkedEntityType,
    string? LinkedEntityId,
    DateTimeOffset? FinalizedAt,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset DateCreated,
    string? UserCreated,
    IReadOnlyList<DocumentVersionView> Versions,
    DocumentAbilities Can);

/// <summary>
/// Versioned documents on top of <see cref="IFileService"/>: every upload runs the same checks (type, size, content
/// signature, <see cref="IFileUploadCheck"/>s) and goes to the storage provider chosen for its type.
/// </summary>
public interface IDocumentService
{
    Task<ValidationResponse<DocumentDetails>> CreateAsync(CreateDocumentRequest request, CancellationToken cancellationToken = default);

    Task<ValidationResponse<DocumentDetails>> AddVersionAsync(Guid documentId, DocumentUpload file, string? comment, CancellationToken cancellationToken = default);

    Task<ValidationResponse<DocumentDetails>> UpdateAsync(Guid documentId, UpdateDocumentRequest request, CancellationToken cancellationToken = default);

    Task<ValidationResponse> FinalizeAsync(Guid documentId, CancellationToken cancellationToken = default);

    Task<ValidationResponse> ReopenAsync(Guid documentId, CancellationToken cancellationToken = default);

    Task<ValidationResponse> ArchiveAsync(Guid documentId, CancellationToken cancellationToken = default);

    Task<ValidationResponse> RestoreAsync(Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes the document, its versions and their files; the bytes are purged after <c>Files.DeletedRetentionDays</c>.</summary>
    Task<ValidationResponse> DeleteAsync(Guid documentId, CancellationToken cancellationToken = default);

    Task<ValidationResponse<DocumentDetails>> GetAsync(Guid documentId, CancellationToken cancellationToken = default);

    Task<PagedResult<DocumentSummary>> SearchAsync(DocumentQuery query, CancellationToken cancellationToken = default);

    /// <summary>The latest version, or <paramref name="version"/>.</summary>
    Task<ValidationResponse<FileDownload>> DownloadAsync(Guid documentId, int? version = null, CancellationToken cancellationToken = default);
}

public enum DocumentAction
{
    Read = 0,

    /// <summary>Change details, add versions, finalise, delete a draft.</summary>
    Edit = 1,

    /// <summary>Reopen, archive, restore, delete anything.</summary>
    Manage = 2,
}

/// <summary>
/// Who may do what with documents. Replace or decorate per project (e.g. "may read documents linked to invoices
/// they can see"). A document the user can't read looks missing, so ids can't be probed.
/// </summary>
public interface IDocumentAccessPolicy
{
    /// <summary>True when lists may show every document in the tenant; otherwise they show only the user's own.</summary>
    bool CanSeeAll { get; }

    bool CanCreate { get; }

    bool Can(Document document, DocumentAction action);
}
