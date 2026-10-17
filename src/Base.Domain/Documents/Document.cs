using Base.Domain.Common;

namespace Base.Domain.Documents;

public enum DocumentStatus
{
    /// <summary>Being worked on: new versions can be added.</summary>
    Draft = 0,

    /// <summary>Signed off: locked against new versions until reopened.</summary>
    Final = 1,

    /// <summary>Out of day-to-day lists; kept for retention and can be restored.</summary>
    Archived = 2,
}

public enum DocumentSource
{
    Uploaded = 0,
    Generated = 1,
}

public sealed record DocumentVersionAdded(Guid DocumentId, int Number) : IDomainEvent;

public sealed record DocumentStatusChanged(Guid DocumentId, DocumentStatus Status) : IDomainEvent;

/// <summary>
/// A titled, typed, versioned document, optionally linked to any record (<c>LinkedEntityType</c>/<c>Id</c>, e.g.
/// <c>Invoice</c>/<c>42</c>). Each version's bytes are a <see cref="Files.StoredFile"/>; the document holds the history.
/// </summary>
public sealed class Document : EntityBase, ITenantOwned
{
    public const int TitleMaxLength = 200;
    public const int TypeMaxLength = 50;
    public const int DescriptionMaxLength = 2000;
    public const int LinkTypeMaxLength = 100;
    public const int LinkIdMaxLength = 100;
    public const int TagMaxLength = 50;
    public const int MaxTags = 20;

    private readonly List<DocumentVersion> _versions = [];
    private List<string> _tags = [];

    private Document()
    {
    }

    public Document(string title, string documentType, string? description, IEnumerable<string>? tags, string? linkedEntityType, string? linkedEntityId, Guid? createdByUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        if (string.IsNullOrWhiteSpace(linkedEntityType) != string.IsNullOrWhiteSpace(linkedEntityId))
        {
            throw new ArgumentException("A link needs both an entity type and an id.", nameof(linkedEntityId));
        }

        Id = SequentialGuid.Next();
        DocumentType = documentType;
        LinkedEntityType = string.IsNullOrWhiteSpace(linkedEntityType) ? null : linkedEntityType.Trim();
        LinkedEntityId = string.IsNullOrWhiteSpace(linkedEntityId) ? null : linkedEntityId.Trim();
        CreatedByUserId = createdByUserId;
        Update(title, description, tags);
    }

    public Guid TenantId { get; set; }

    public string Title { get; private set; } = string.Empty;

    /// <summary>A <c>DocumentTypeCatalog</c> code; also the file category of its versions.</summary>
    public string DocumentType { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>Lower-case labels for search. Stored as JSON.</summary>
    public IReadOnlyList<string> Tags => _tags;

    public string? LinkedEntityType { get; private set; }

    public string? LinkedEntityId { get; private set; }

    public DocumentStatus Status { get; private set; }

    /// <summary>The latest version's number (versions are 1, 2, 3...); 0 before the first is added.</summary>
    public int CurrentVersion { get; private set; }

    public Guid? CreatedByUserId { get; private set; }

    public DateTimeOffset? FinalizedAt { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public IReadOnlyCollection<DocumentVersion> Versions => _versions.AsReadOnly();

    public void Update(string title, string? description, IEnumerable<string>? tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        EnsureNotArchived();
        Title = title.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        _tags = NormalizeTags(tags);
    }

    /// <summary>Adds the next version. Only drafts take new versions, so a final document never changes silently.</summary>
    public DocumentVersion AddVersion(Guid fileId, string fileName, string contentType, long sizeBytes, string sha256, DocumentSource source, string? comment, string? templateCode)
    {
        if (Status != DocumentStatus.Draft)
        {
            throw new InvalidOperationException("Only draft documents take new versions. Reopen it first.");
        }

        var version = new DocumentVersion(Id, CurrentVersion + 1, fileId, fileName, contentType, sizeBytes, sha256, source, comment, templateCode)
        {
            TenantId = TenantId,
        };
        _versions.Add(version);
        CurrentVersion = version.Number;
        Raise(new DocumentVersionAdded(Id, version.Number));
        return version;
    }

    public void MarkFinal(DateTimeOffset now)
    {
        if (Status != DocumentStatus.Draft)
        {
            throw new InvalidOperationException("Only draft documents can be finalised.");
        }

        if (CurrentVersion == 0)
        {
            throw new InvalidOperationException("A document needs a version before it can be finalised.");
        }

        FinalizedAt = now;
        SetStatus(DocumentStatus.Final);
    }

    public void Reopen()
    {
        if (Status != DocumentStatus.Final)
        {
            throw new InvalidOperationException("Only final documents can be reopened.");
        }

        FinalizedAt = null;
        SetStatus(DocumentStatus.Draft);
    }

    public void Archive(DateTimeOffset now)
    {
        EnsureNotArchived();
        ArchivedAt = now;
        SetStatus(DocumentStatus.Archived);
    }

    /// <summary>Back to what it was before archiving: final if it had been finalised, otherwise draft.</summary>
    public void Restore()
    {
        if (Status != DocumentStatus.Archived)
        {
            throw new InvalidOperationException("Only archived documents can be restored.");
        }

        ArchivedAt = null;
        SetStatus(FinalizedAt is null ? DocumentStatus.Draft : DocumentStatus.Final);
    }

    private static List<string> NormalizeTags(IEnumerable<string>? tags)
    {
        var result = (tags ?? [])
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (result.Count > MaxTags || result.Exists(t => t.Length > TagMaxLength))
        {
            throw new ArgumentException($"Up to {MaxTags} tags of at most {TagMaxLength} characters.", nameof(tags));
        }

        return result;
    }

    private void EnsureNotArchived()
    {
        if (Status == DocumentStatus.Archived)
        {
            throw new InvalidOperationException("Archived documents are read-only. Restore it first.");
        }
    }

    private void SetStatus(DocumentStatus status)
    {
        Status = status;
        Raise(new DocumentStatusChanged(Id, status));
    }
}

/// <summary>One immutable version of a <see cref="Document"/>: which file holds it and where it came from.</summary>
public sealed class DocumentVersion : EntityBase, ITenantOwned
{
    public const int CommentMaxLength = 500;
    public const int TemplateCodeMaxLength = 100;

    private DocumentVersion()
    {
    }

    internal DocumentVersion(Guid documentId, int number, Guid fileId, string fileName, string contentType, long sizeBytes, string sha256, DocumentSource source, string? comment, string? templateCode)
    {
        Id = SequentialGuid.Next();
        DocumentId = documentId;
        Number = number;
        FileId = fileId;
        FileName = fileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        Source = source;
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        TemplateCode = templateCode;
    }

    public Guid TenantId { get; set; }

    public Guid DocumentId { get; private set; }

    public int Number { get; private set; }

    /// <summary>The <see cref="Files.StoredFile"/> holding this version's bytes.</summary>
    public Guid FileId { get; private set; }

    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    public string Sha256 { get; private set; } = string.Empty;

    public DocumentSource Source { get; private set; }

    public string? Comment { get; private set; }

    /// <summary>For generated versions: the template it was produced from.</summary>
    public string? TemplateCode { get; private set; }
}
