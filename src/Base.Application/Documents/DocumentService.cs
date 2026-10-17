using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Files;
using Base.Domain.Documents;
using Base.Domain.Files;

namespace Base.Application.Documents;

internal sealed class DocumentService(
    IUnitOfWork unitOfWork,
    FileService files,
    IDocumentAccessPolicy policy,
    ICurrentUser currentUser,
    TimeProvider time) : IDocumentService
{
    private const string NotFoundMessage = "Document not found.";

    public Task<ValidationResponse<DocumentDetails>> CreateAsync(CreateDocumentRequest request, CancellationToken cancellationToken = default) =>
        CreateCoreAsync(request, DocumentSource.Uploaded, null, null, finalize: false, cancellationToken);

    public Task<ValidationResponse<DocumentDetails>> AddVersionAsync(Guid documentId, DocumentUpload file, string? comment, CancellationToken cancellationToken = default) =>
        AddVersionCoreAsync(documentId, file, comment, DocumentSource.Uploaded, null, null, cancellationToken);

    /// <param name="generatedExtensions">For rendered output: the only extensions accepted (the upload allow-list doesn't apply).</param>
    internal async Task<ValidationResponse<DocumentDetails>> CreateCoreAsync(
        CreateDocumentRequest request, DocumentSource source, string? templateCode, IReadOnlyCollection<string>? generatedExtensions, bool finalize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!policy.CanCreate)
        {
            return ValidationResponse.Forbidden().As<DocumentDetails>();
        }

        var type = DocumentTypeCatalog.Find(request.DocumentType);
        if (type is null)
        {
            return Invalid(nameof(request.DocumentType), $"Unknown document type '{request.DocumentType}'.");
        }

        var invalid = ValidateDetails(request.Title, request.Description, request.Tags) ?? ValidateLink(request.LinkedTo) ?? ValidateComment(request.Comment);
        if (invalid is not null)
        {
            return invalid;
        }

        var document = new Document(request.Title, type.Code, request.Description, request.Tags, request.LinkedTo?.EntityType, request.LinkedTo?.EntityId, currentUser.UserId);
        var staged = await StageAsync(type, request.File, generatedExtensions, cancellationToken);
        if (!staged.IsSuccess)
        {
            return staged.As<DocumentDetails>();
        }

        var file = staged.Data!.File;
        document.AddVersion(file.Id, file.OriginalFileName, file.ContentType, file.SizeBytes, file.Sha256, source, request.Comment, templateCode);
        if (finalize)
        {
            document.MarkFinal(time.GetUtcNow());
        }

        await unitOfWork.Repository<Document>().AddAsync(document, cancellationToken);
        await staged.Data.SaveOrDiscardAsync(unitOfWork, cancellationToken);
        return ToDetails(document);
    }

    internal async Task<ValidationResponse<DocumentDetails>> AddVersionCoreAsync(
        Guid documentId, DocumentUpload upload, string? comment, DocumentSource source, string? templateCode, IReadOnlyCollection<string>? generatedExtensions, CancellationToken cancellationToken)
    {
        var document = await LoadAsync(documentId, DocumentAction.Edit, includeVersions: true, cancellationToken);
        if (document is null)
        {
            return ValidationResponse.NotFound(NotFoundMessage).As<DocumentDetails>();
        }

        if (document.Status != DocumentStatus.Draft)
        {
            return ValidationResponse.Conflict("Only draft documents take new versions. Reopen it first.").As<DocumentDetails>();
        }

        if (ValidateComment(comment) is { } invalid)
        {
            return invalid;
        }

        var type = DocumentTypeCatalog.Find(document.DocumentType)
            ?? throw new InvalidOperationException($"Document type '{document.DocumentType}' is no longer in the DocumentTypeCatalog.");
        var staged = await StageAsync(type, upload, generatedExtensions, cancellationToken);
        if (!staged.IsSuccess)
        {
            return staged.As<DocumentDetails>();
        }

        var file = staged.Data!.File;
        document.AddVersion(file.Id, file.OriginalFileName, file.ContentType, file.SizeBytes, file.Sha256, source, comment, templateCode);
        await staged.Data.SaveOrDiscardAsync(unitOfWork, cancellationToken);
        return ToDetails(document);
    }

    public async Task<ValidationResponse<DocumentDetails>> UpdateAsync(Guid documentId, UpdateDocumentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var document = await LoadAsync(documentId, DocumentAction.Edit, includeVersions: true, cancellationToken);
        if (document is null)
        {
            return ValidationResponse.NotFound(NotFoundMessage).As<DocumentDetails>();
        }

        if (document.Status == DocumentStatus.Archived)
        {
            return ValidationResponse.Conflict("Archived documents are read-only. Restore it first.").As<DocumentDetails>();
        }

        if (ValidateDetails(request.Title, request.Description, request.Tags) is { } invalid)
        {
            return invalid;
        }

        document.Update(request.Title, request.Description, request.Tags);
        await unitOfWork.SaveAsync(cancellationToken);
        return ToDetails(document);
    }

    public Task<ValidationResponse> FinalizeAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(documentId, DocumentAction.Edit, DocumentStatus.Draft, "Only draft documents can be finalised.", d => d.MarkFinal(time.GetUtcNow()), "Document finalised.", cancellationToken);

    public Task<ValidationResponse> ReopenAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(documentId, DocumentAction.Manage, DocumentStatus.Final, "Only final documents can be reopened.", d => d.Reopen(), "Document reopened as a draft.", cancellationToken);

    public async Task<ValidationResponse> ArchiveAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(documentId, DocumentAction.Manage, includeVersions: false, cancellationToken);
        if (document is null)
        {
            return ValidationResponse.NotFound(NotFoundMessage);
        }

        if (document.Status == DocumentStatus.Archived)
        {
            return ValidationResponse.Conflict("The document is already archived.");
        }

        document.Archive(time.GetUtcNow());
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success("Document archived.");
    }

    public Task<ValidationResponse> RestoreAsync(Guid documentId, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(documentId, DocumentAction.Manage, DocumentStatus.Archived, "Only archived documents can be restored.", d => d.Restore(), "Document restored.", cancellationToken);

    public async Task<ValidationResponse> DeleteAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(documentId, DocumentAction.Edit, includeVersions: true, cancellationToken);
        if (document is null)
        {
            return ValidationResponse.NotFound(NotFoundMessage);
        }

        await RemoveAsync(unitOfWork, document, cancellationToken);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success("Document deleted.");
    }

    /// <summary>Stages the soft delete of a document (loaded with its versions), its versions and their files.</summary>
    internal static async Task RemoveAsync(IUnitOfWork unitOfWork, Document document, CancellationToken cancellationToken)
    {
        var fileIds = document.Versions.Select(v => v.FileId).ToList();
        var storedFiles = unitOfWork.Repository<StoredFile>();
        foreach (var file in await storedFiles.GetAllAsync(filter: f => fileIds.Contains(f.Id), cancellationToken: cancellationToken))
        {
            storedFiles.Remove(file);
        }

        var versions = unitOfWork.Repository<DocumentVersion>();
        foreach (var version in document.Versions)
        {
            versions.Remove(version);
        }

        unitOfWork.Repository<Document>().Remove(document);
    }

    public async Task<ValidationResponse<DocumentDetails>> GetAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(documentId, DocumentAction.Read, includeVersions: true, cancellationToken, asNoTracking: true);
        return document is null ? ValidationResponse.NotFound(NotFoundMessage).As<DocumentDetails>() : ToDetails(document);
    }

    public Task<PagedResult<DocumentSummary>> SearchAsync(DocumentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        Guid? onlyCreatedBy = policy.CanSeeAll ? null : currentUser.UserId ?? Guid.Empty;
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var type = string.IsNullOrWhiteSpace(query.DocumentType) ? null : query.DocumentType;
        var tag = string.IsNullOrWhiteSpace(query.Tag) ? null : query.Tag.Trim().ToLowerInvariant();
        var linkType = query.LinkedTo?.EntityType;
        var linkId = query.LinkedTo?.EntityId;
        var status = query.Status;
        var includeArchived = query.IncludeArchived || status == DocumentStatus.Archived;

        // The Tenant and SoftDelete filters apply on top of this.
        return unitOfWork.Repository<Document>().GetPagedAsync(
            new PageRequest(query.Page, query.PageSize),
            d => new DocumentSummary(d.Id, d.Title, d.DocumentType, d.Status, d.CurrentVersion, d.Tags, d.LinkedEntityType, d.LinkedEntityId, d.DateCreated, d.DateModified, d.UserCreated),
            q => q.OrderByDescending(d => d.DateModified).ThenByDescending(d => d.Id),
            d => (onlyCreatedBy == null || d.CreatedByUserId == onlyCreatedBy)
                && (search == null || d.Title.Contains(search))
                && (type == null || d.DocumentType == type)
                && (tag == null || d.Tags.Contains(tag))
                && (status == null || d.Status == status)
                && (includeArchived || d.Status != DocumentStatus.Archived)
                && (linkType == null || (d.LinkedEntityType == linkType && d.LinkedEntityId == linkId)),
            cancellationToken);
    }

    public async Task<ValidationResponse<FileDownload>> DownloadAsync(Guid documentId, int? version = null, CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(documentId, DocumentAction.Read, includeVersions: true, cancellationToken, asNoTracking: true);
        var wanted = document?.Versions.FirstOrDefault(v => v.Number == (version ?? document.CurrentVersion));
        if (wanted is null)
        {
            return ValidationResponse.NotFound(NotFoundMessage).As<FileDownload>();
        }

        var file = await unitOfWork.Repository<StoredFile>().GetAsync(f => f.Id == wanted.FileId, asNoTracking: true, cancellationToken: cancellationToken);
        if (file is null)
        {
            return ValidationResponse.NotFound(NotFoundMessage).As<FileDownload>();
        }

        return await files.OpenAsync(file, wanted.FileName, cancellationToken);
    }

    private async Task<ValidationResponse> ChangeStatusAsync(
        Guid documentId, DocumentAction action, DocumentStatus from, string wrongStatus, Action<Document> change, string done, CancellationToken cancellationToken)
    {
        var document = await LoadAsync(documentId, action, includeVersions: false, cancellationToken);
        if (document is null)
        {
            return ValidationResponse.NotFound(NotFoundMessage);
        }

        if (document.Status != from)
        {
            return ValidationResponse.Conflict(wrongStatus);
        }

        change(document);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success(done);
    }

    /// <summary>The document when it exists and <paramref name="action"/> is allowed; otherwise null (looks missing).</summary>
    private async Task<Document?> LoadAsync(Guid documentId, DocumentAction action, bool includeVersions, CancellationToken cancellationToken, bool asNoTracking = false)
    {
        var repository = unitOfWork.Repository<Document>();
        var document = includeVersions
            ? await repository.GetAsync(d => d.Id == documentId, [d => d.Versions], asNoTracking, cancellationToken)
            : await repository.GetAsync(d => d.Id == documentId, asNoTracking: asNoTracking, cancellationToken: cancellationToken);
        return document is not null && policy.Can(document, DocumentAction.Read) && policy.Can(document, action) ? document : null;
    }

    private Task<ValidationResponse<StagedFile>> StageAsync(DocumentTypeDefinition type, DocumentUpload upload, IReadOnlyCollection<string>? generatedExtensions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);

        // The document owns the file: no owner, so it's reachable only through this service (and Files.ReadAll).
        var request = new FileUploadRequest(upload.Content, upload.FileName, null, type.Code);
        return generatedExtensions is null
            ? files.StageAsync(request, null, type.AllowedExtensions, serverGenerated: false, cancellationToken)
            : files.StageAsync(request, null, generatedExtensions, serverGenerated: true, cancellationToken);
    }

    private DocumentDetails ToDetails(Document document) => new(
        document.Id,
        document.Title,
        document.DocumentType,
        document.Description,
        document.Status,
        document.CurrentVersion,
        document.Tags,
        document.LinkedEntityType,
        document.LinkedEntityId,
        document.FinalizedAt,
        document.ArchivedAt,
        document.DateCreated,
        document.UserCreated,
        document.Versions
            .OrderByDescending(v => v.Number)
            .Select(v => new DocumentVersionView(v.Number, v.FileName, v.ContentType, v.SizeBytes, v.Sha256, v.Source, v.Comment, v.TemplateCode, v.DateCreated, v.UserCreated))
            .ToList(),
        new DocumentAbilities(policy.Can(document, DocumentAction.Edit), policy.Can(document, DocumentAction.Manage)));

    private static ValidationResponse<DocumentDetails>? ValidateDetails(string? title, string? description, IReadOnlyList<string>? tags)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > Document.TitleMaxLength)
        {
            return Invalid("Title", $"A title of up to {Document.TitleMaxLength} characters is required.");
        }

        if (description?.Trim().Length > Document.DescriptionMaxLength)
        {
            return Invalid("Description", $"The description can be at most {Document.DescriptionMaxLength} characters.");
        }

        var cleaned = (tags ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (cleaned.Count > Document.MaxTags || cleaned.Exists(t => t.Length > Document.TagMaxLength))
        {
            return Invalid("Tags", $"Up to {Document.MaxTags} tags of at most {Document.TagMaxLength} characters each.");
        }

        return null;
    }

    private static ValidationResponse<DocumentDetails>? ValidateLink(DocumentLink? link)
    {
        if (link is null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(link.EntityType) || string.IsNullOrWhiteSpace(link.EntityId)
            || link.EntityType.Trim().Length > Document.LinkTypeMaxLength || link.EntityId.Trim().Length > Document.LinkIdMaxLength
            ? Invalid("LinkedTo", "A link needs an entity type (up to 100 characters) and an id (up to 100 characters).")
            : null;
    }

    private static ValidationResponse<DocumentDetails>? ValidateComment(string? comment) =>
        comment?.Trim().Length > DocumentVersion.CommentMaxLength
            ? Invalid("Comment", $"The comment can be at most {DocumentVersion.CommentMaxLength} characters.")
            : null;

    private static ValidationResponse<DocumentDetails> Invalid(string field, string message) =>
        ValidationResponse.Failure(new ValidationError(field, message, "document.invalid")).As<DocumentDetails>();
}
