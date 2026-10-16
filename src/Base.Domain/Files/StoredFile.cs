using Base.Domain.Common;

namespace Base.Domain.Files;

/// <summary>
/// Metadata for a file held in object storage. The bytes never live in SQL.
/// <see cref="ProviderName"/> records where the object lives so files survive a change of default provider.
/// </summary>
public sealed class StoredFile : EntityBase, ITenantOwned
{
    public const int ProviderNameMaxLength = 50;
    public const int ObjectKeyMaxLength = 512;
    public const int FileNameMaxLength = 255;
    public const int ContentTypeMaxLength = 127;
    public const int CategoryMaxLength = 100;
    public const int Sha256Length = 64;

    private StoredFile()
    {
    }

    public StoredFile(
        string providerName,
        string objectKey,
        string originalFileName,
        string contentType,
        long sizeBytes,
        string sha256,
        string category,
        Guid? ownerUserId,
        DateTimeOffset? expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);

        ProviderName = providerName;
        ObjectKey = objectKey;
        OriginalFileName = originalFileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        Category = category;
        OwnerUserId = ownerUserId;
        ExpiresAt = expiresAt;
    }

    public Guid TenantId { get; set; }

    public string ProviderName { get; private set; } = string.Empty;

    /// <summary>Provider object key, <c>{tenant}/{category}/{yyyy}/{MM}/{guid}{ext}</c>. Never the user's file name.</summary>
    public string ObjectKey { get; private set; } = string.Empty;

    public string OriginalFileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    /// <summary>Lower-case hex SHA-256 of the content: tamper checks and de-duplication.</summary>
    public string Sha256 { get; private set; } = string.Empty;

    /// <summary>What the file is for (e.g. <c>avatar</c>, <c>invoice</c>). Drives validation rules and access checks.</summary>
    public string Category { get; private set; } = string.Empty;

    public Guid? OwnerUserId { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiresAt && expiresAt <= now;
}
