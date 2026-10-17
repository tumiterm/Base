using Base.Application.Common.Abstractions;
using Base.Application.Files;
using Base.Application.Settings;
using Base.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Base.Infrastructure.Files;

/// <summary>
/// Keeps file bytes in SQL Server, for small files that should live and be backed up with the data (signatures,
/// avatars, logos). Capped by the <c>Files.Database.MaxFileSizeKb</c> and <c>Files.Database.TenantQuotaMb</c>
/// settings, because large blobs bloat backups, the buffer pool and the log. Choose it per category with
/// <c>Files.CategoryProviders</c>. Uses its own scope (like the other providers, a separate commit from the metadata).
/// </summary>
internal sealed class DatabaseFileStorageProvider(IServiceScopeFactory scopes, TimeProvider time) : IFileStorageProvider
{
    private const long BytesPerMb = 1024L * 1024L;

    public string Name => FileStorageProviderNames.Database;

    public async Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var tenantId = ObjectKeys.TenantOf(objectKey) ?? throw new ArgumentException("Object key has no tenant.", nameof(objectKey));

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, null);
        var settings = scope.ServiceProvider.GetRequiredService<IApplicationSettingService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var maxKb = await settings.GetIntAsync(SettingCodes.Files.DatabaseMaxFileSizeKb, cancellationToken);
        var bytes = await ReadUpToAsync(content, maxKb * 1024L, cancellationToken)
            ?? throw new FileStorageLimitException($"Files stored in the database may be at most {maxKb} KB.");

        var quotaMb = await settings.GetIntAsync(SettingCodes.Files.DatabaseTenantQuotaMb, cancellationToken);
        if (quotaMb > 0)
        {
            // A soft limit: two concurrent uploads can both pass. Good enough for a guard against runaway growth.
            var used = await db.FileContents.Where(c => c.TenantId == tenantId).SumAsync(c => (long?)c.SizeBytes, cancellationToken) ?? 0;
            if (used + bytes.Length > quotaMb * BytesPerMb)
            {
                throw new FileStorageLimitException(
                    $"Database file storage for this tenant is full ({used / BytesPerMb} of {quotaMb} MB used). Delete files or ask an administrator to raise the quota.");
            }
        }

        db.FileContents.Add(new FileContent { ObjectKey = objectKey, TenantId = tenantId, SizeBytes = bytes.Length, Content = bytes, CreatedAt = time.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var bytes = await scope.ServiceProvider.GetRequiredService<AppDbContext>().FileContents
            .Where(c => c.ObjectKey == objectKey)
            .Select(c => c.Content)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new FileNotFoundException("Stored object not found.", objectKey);
        return new MemoryStream(bytes, writable: false);
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        // Bytes only: the audited metadata row is StoredFile, so a bulk delete here bypasses nothing.
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().FileContents
            .Where(c => c.ObjectKey == objectKey)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().FileContents.AnyAsync(c => c.ObjectKey == objectKey, cancellationToken);
    }

    public Task<Uri?> GetDownloadUrlAsync(string objectKey, string downloadFileName, string contentType, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        Task.FromResult<Uri?>(null);

    /// <summary>The whole stream, or null as soon as it is longer than <paramref name="maxBytes"/>.</summary>
    private static async Task<byte[]?> ReadUpToAsync(Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return buffer.ToArray();
    }
}
