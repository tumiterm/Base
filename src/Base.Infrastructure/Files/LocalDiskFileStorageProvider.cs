using Base.Application.Files;

namespace Base.Infrastructure.Files;

/// <summary>Stores objects on the local file system. Development and tests only: not shared between instances.</summary>
internal sealed class LocalDiskFileStorageProvider(string rootPath) : IFileStorageProvider
{
    private readonly string _root = Path.GetFullPath(rootPath);

    public string Name => "LocalDisk";

    public async Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var path = Resolve(objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(target, cancellationToken);
    }

    public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new FileStream(Resolve(objectKey), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        File.Delete(Resolve(objectKey));
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(Resolve(objectKey)));

    public Task<Uri?> GetDownloadUrlAsync(string objectKey, string downloadFileName, string contentType, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        Task.FromResult<Uri?>(null);

    private string Resolve(string objectKey)
    {
        // Keys are always relative (see ObjectKeys); an absolute one is a bug or an attack.
        if (Path.IsPathRooted(objectKey))
        {
            throw new ArgumentException("Object keys must be relative.", nameof(objectKey));
        }

        // Join (unlike Combine) never discards the root; the check below catches "..".
        var path = Path.GetFullPath(Path.Join(_root, objectKey));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Object key escapes the storage root.", nameof(objectKey));
        }

        return path;
    }
}
