namespace Base.Infrastructure.Files;

/// <summary>
/// The bytes of a file kept by <see cref="DatabaseFileStorageProvider"/>, in their own table so metadata queries on
/// <c>StoredFiles</c> never read them. Storage plumbing like the outbox: not an entity, not audited, no query filters.
/// </summary>
public sealed class FileContent
{
    public string ObjectKey { get; set; } = string.Empty;

    /// <summary>From the object key; what the per-tenant quota is summed over.</summary>
    public Guid TenantId { get; set; }

    public long SizeBytes { get; set; }

    public byte[] Content { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}
