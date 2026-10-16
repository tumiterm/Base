using System.Security.Cryptography;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Settings;
using Base.Domain.Common;
using Base.Domain.Files;
using Microsoft.Extensions.Logging;

namespace Base.Application.Files;

internal sealed partial class FileService(
    IUnitOfWork unitOfWork,
    IFileStorageProviderResolver providers,
    IStoredFileAccessPolicy accessPolicy,
    IApplicationSettingService settings,
    ITenantContext tenant,
    ICurrentUser currentUser,
    TimeProvider time,
    ILogger<FileService> logger) : IFileService
{
    private const int BufferSize = 81920;

    public async Task<ValidationResponse<FileUploadResult>> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fileName = SanitizeFileName(request.FileName);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (fileName.Length is 0 or > StoredFile.FileNameMaxLength)
        {
            return Fail("A file name of up to 255 characters is required.");
        }

        if (!ObjectKeys.IsValidCategory(request.Category))
        {
            return Fail("Invalid file category.", nameof(request.Category));
        }

        var allowed = await settings.GetListAsync(SettingCodes.Files.AllowedExtensions, cancellationToken);
        if (!allowed.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return Fail($"Files of type '{extension}' are not allowed. Allowed: {string.Join(", ", allowed)}.");
        }

        var maxBytes = await settings.GetIntAsync(SettingCodes.Files.MaxUploadSizeMb, cancellationToken) * 1024L * 1024L;

        // Spool to a temp file while hashing and measuring, so we never trust Content-Length and never hold the file in memory.
        await using var spool = new FileStream(
            Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var header = new byte[FileSignatures.HeaderLength];
        var headerLength = 0;
        long size = 0;
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await request.Content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            size += read;
            if (size > maxBytes)
            {
                return Fail($"The file is larger than the {maxBytes / 1024 / 1024} MB limit.");
            }

            if (headerLength < header.Length)
            {
                var take = Math.Min(header.Length - headerLength, read);
                Array.Copy(buffer, 0, header, headerLength, take);
                headerLength += take;
            }

            hash.AppendData(buffer, 0, read);
            await spool.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (size == 0)
        {
            return Fail("The file is empty.");
        }

        if (!FileSignatures.Matches(extension, header.AsSpan(0, headerLength)))
        {
            return Fail($"The file content does not match its '{extension}' extension.");
        }

        var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        var contentType = FileSignatures.ContentTypeFor(extension);
        var provider = await ResolveUploadProviderAsync(cancellationToken);
        var fileId = SequentialGuid.Next();
        var objectKey = ObjectKeys.Build(tenant.TenantId, request.Category, time.GetUtcNow(), fileId, extension);

        spool.Position = 0;
        await provider.UploadAsync(objectKey, spool, contentType, cancellationToken);

        var file = new StoredFile(provider.Name, objectKey, fileName, contentType, size, sha256, request.Category, currentUser.UserId, request.ExpiresAt)
        {
            Id = fileId,
        };

        try
        {
            await unitOfWork.Repository<StoredFile>().AddAsync(file, cancellationToken);
            await unitOfWork.SaveAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Compensate: don't leave an orphaned object behind when the metadata row can't be written.
            LogOrphanCleanup(logger, objectKey, provider.Name);
            await provider.DeleteAsync(objectKey, CancellationToken.None);
            throw;
        }

        return new FileUploadResult(file.Id, fileName, contentType, size, sha256);
    }

    public async Task<ValidationResponse<FileDownload>> GetDownloadAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var file = await unitOfWork.Repository<StoredFile>().GetAsync(f => f.Id == fileId, asNoTracking: true, cancellationToken: cancellationToken);
        if (file is null || file.IsExpired(time.GetUtcNow()))
        {
            return ValidationResponse.NotFound("File not found.").As<FileDownload>();
        }

        if (!await accessPolicy.CanAccessAsync(file, FileAccessPurpose.Read, cancellationToken))
        {
            // Same answer as "missing", so ids can't be probed.
            return ValidationResponse.NotFound("File not found.").As<FileDownload>();
        }

        var provider = providers.Get(file.ProviderName);
        var minutes = await settings.GetIntAsync(SettingCodes.Files.DownloadLinkMinutes, cancellationToken);
        var url = await provider.GetDownloadUrlAsync(file.ObjectKey, file.OriginalFileName, file.ContentType, TimeSpan.FromMinutes(minutes), cancellationToken);

        return url is not null
            ? new FileDownload(file.OriginalFileName, file.ContentType, url, null)
            : new FileDownload(file.OriginalFileName, file.ContentType, null, await provider.OpenReadAsync(file.ObjectKey, cancellationToken));
    }

    public async Task<ValidationResponse> DeleteAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.Repository<StoredFile>();
        var file = await repository.GetAsync(f => f.Id == fileId, cancellationToken: cancellationToken);
        if (file is null || !await accessPolicy.CanAccessAsync(file, FileAccessPurpose.Delete, cancellationToken))
        {
            return ValidationResponse.NotFound("File not found.");
        }

        // Soft delete only: the object stays until a retention job purges it, so deletes can be undone and audited.
        repository.Remove(file);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success("File deleted.");
    }

    private async Task<IFileStorageProvider> ResolveUploadProviderAsync(CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(SettingCodes.Files.DefaultProvider, cancellationToken);
        if (configured is not null && providers.TryGet(configured, out var provider))
        {
            return provider;
        }

        LogProviderFallback(logger, configured);
        return providers.Get(providers.Names.First());
    }

    /// <summary>
    /// Display name only (the object key never uses it): strips any client path, Windows or Unix, and control characters.
    /// </summary>
    internal static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        return new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
    }

    private static ValidationResponse<FileUploadResult> Fail(string message, string field = "file") =>
        ValidationResponse.Failure(new ValidationError(field, message, "file.invalid")).As<FileUploadResult>();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Metadata save failed; deleting orphaned object {ObjectKey} from {Provider}")]
    private static partial void LogOrphanCleanup(ILogger logger, string objectKey, string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configured file provider {Provider} is not registered; using the first registered provider")]
    private static partial void LogProviderFallback(ILogger logger, string? provider);
}
