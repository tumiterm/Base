using System.Diagnostics.CodeAnalysis;

namespace Base.Application.Files;

/// <summary>
/// Stores bytes under an object key. Implementations: S3-compatible (AWS S3, Cloudflare R2, MinIO),
/// Azure Blob, and LocalDisk for development/tests. There is deliberately no database provider.
/// </summary>
public interface IFileStorageProvider
{
    /// <summary>Stable name persisted on each <see cref="Domain.Files.StoredFile"/> (e.g. <c>S3</c>).</summary>
    string Name { get; }

    Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default);

    Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>A short-lived direct download link, or null when the provider can't issue one (the app then streams).</summary>
    Task<Uri?> GetDownloadUrlAsync(string objectKey, string downloadFileName, string contentType, TimeSpan lifetime, CancellationToken cancellationToken = default);
}

/// <summary>Finds providers by name. Files are read from the provider recorded on the file, not the current default.</summary>
public interface IFileStorageProviderResolver
{
    IReadOnlyCollection<string> Names { get; }

    IFileStorageProvider Get(string name);

    bool TryGet(string name, [MaybeNullWhen(false)] out IFileStorageProvider provider);
}
