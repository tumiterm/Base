using System.Security.Cryptography;
using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Settings;
using Base.Application.Subscriptions;
using Base.Domain.Common;
using Base.Domain.Files;
using Microsoft.Extensions.Logging;

namespace Base.Application.Files;

internal sealed partial class FileService(
    IUnitOfWork unitOfWork,
    IFileStorageProviderResolver providers,
    IStoredFileAccessPolicy accessPolicy,
    IApplicationSettingService settings,
    IEnumerable<IFileUploadCheck> checks,
    ITenantContext tenant,
    ITenantEntitlements entitlements,
    ICurrentUser currentUser,
    TimeProvider time,
    ILogger<FileService> logger) : IFileService
{
    private const int BufferSize = 81920;

    public async Task<ValidationResponse<FileUploadResult>> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default)
    {
        var staged = await StageAsync(request, currentUser.UserId, null, replaceAllowList: false, cancellationToken);
        if (!staged.IsSuccess)
        {
            return staged.As<FileUploadResult>();
        }

        await staged.Data!.SaveOrDiscardAsync(unitOfWork, cancellationToken);
        return staged.Data.Result;
    }

    /// <summary>
    /// Validates, checks and stores the bytes, then stages the metadata row without saving, so callers (documents)
    /// can commit it together with their own rows. Callers save through <see cref="StagedFile.SaveOrDiscardAsync"/>.
    /// </summary>
    /// <param name="ownerUserId">Who may read and delete it through <see cref="IFileService"/>; null when another feature owns it.</param>
    /// <param name="allowedExtensions">
    /// A further restriction on top of the <c>Files.AllowedExtensions</c> setting; or, when <paramref name="replaceAllowList"/>,
    /// the only one. The setting governs general uploads; features that take specific formats (generated documents,
    /// exports, import files) pass their own list. Every other check still applies.
    /// </param>
    /// <param name="maxBytes">Replaces the <c>Files.MaxUploadSizeMb</c> limit, for output the application produces itself.</param>
    internal async Task<ValidationResponse<StagedFile>> StageAsync(
        FileUploadRequest request, Guid? ownerUserId, IReadOnlyCollection<string>? allowedExtensions, bool replaceAllowList, CancellationToken cancellationToken, long? maxBytes = null)
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

        IEnumerable<string> allowed = replaceAllowList && allowedExtensions is not null
            ? allowedExtensions
            : await settings.GetListAsync(SettingCodes.Files.AllowedExtensions, cancellationToken);
        if (allowedExtensions is not null && !replaceAllowList)
        {
            allowed = allowed.Intersect(allowedExtensions, StringComparer.OrdinalIgnoreCase).ToList();
        }

        if (!allowed.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return Fail($"Files of type '{extension}' are not allowed. Allowed: {string.Join(", ", allowed)}.");
        }

        maxBytes ??= await settings.GetIntAsync(SettingCodes.Files.MaxUploadSizeMb, cancellationToken) * 1024L * 1024L;

        // Spool to a temp file while hashing and measuring, so we never trust Content-Length and never hold the file in memory.
        await using var spool = new FileStream(
            Path.Join(Path.GetTempPath(), Path.GetRandomFileName()),
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

        foreach (var check in checks.OrderBy(c => c.Order))
        {
            spool.Position = 0;
            var verdict = await check.CheckAsync(new FileUploadCheckContext(fileName, extension, contentType, request.Category, size, sha256, spool), cancellationToken);
            if (!verdict.IsSuccess)
            {
                return Fail(verdict.Message ?? "The file was rejected.");
            }
        }

        var quota = await entitlements.EnsureWithinLimitAsync(LimitCodes.StorageMb, size, cancellationToken);
        if (quota.IsError)
        {
            return Fail(quota.Message ?? "The plan's storage is full.");
        }

        var provider = await ResolveUploadProviderAsync(request.Category, cancellationToken);
        var fileId = SequentialGuid.Next();
        var objectKey = ObjectKeys.Build(tenant.TenantId, request.Category, time.GetUtcNow(), fileId, extension);

        spool.Position = 0;
        try
        {
            await provider.UploadAsync(objectKey, spool, contentType, cancellationToken);
        }
        catch (FileStorageLimitException limit)
        {
            return Fail(limit.Message);
        }

        var file = new StoredFile(provider.Name, objectKey, fileName, contentType, size, sha256, request.Category, ownerUserId, request.ExpiresAt)
        {
            Id = fileId,
        };

        await unitOfWork.Repository<StoredFile>().AddAsync(file, cancellationToken);
        return new StagedFile(file, provider, logger);
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

        return await OpenAsync(file, file.OriginalFileName, cancellationToken);
    }

    /// <summary>Opens a file the caller has already authorised (no access policy check).</summary>
    internal async Task<FileDownload> OpenAsync(StoredFile file, string downloadFileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var provider = providers.Get(file.ProviderName);
        var minutes = await settings.GetIntAsync(SettingCodes.Files.DownloadLinkMinutes, cancellationToken);
        var url = await provider.GetDownloadUrlAsync(file.ObjectKey, downloadFileName, file.ContentType, TimeSpan.FromMinutes(minutes), cancellationToken);

        return url is not null
            ? new FileDownload(downloadFileName, file.ContentType, url, null)
            : new FileDownload(downloadFileName, file.ContentType, null, await provider.OpenReadAsync(file.ObjectKey, cancellationToken));
    }

    /// <summary>The file's bytes from its provider, never a redirect. For server-side reads of authorised files.</summary>
    internal Task<Stream> OpenReadAsync(StoredFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        return providers.Get(file.ProviderName).OpenReadAsync(file.ObjectKey, cancellationToken);
    }

    public async Task<ValidationResponse> DeleteAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var repository = unitOfWork.Repository<StoredFile>();
        var file = await repository.GetAsync(f => f.Id == fileId, cancellationToken: cancellationToken);
        if (file is null || !await accessPolicy.CanAccessAsync(file, FileAccessPurpose.Delete, cancellationToken))
        {
            return ValidationResponse.NotFound("File not found.");
        }

        // Soft delete only: the object stays until the retention job purges it, so deletes can be undone and audited.
        repository.Remove(file);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success("File deleted.");
    }

    private async Task<IFileStorageProvider> ResolveUploadProviderAsync(string category, CancellationToken cancellationToken)
    {
        var configured = ProviderForCategory(await settings.GetListAsync(SettingCodes.Files.CategoryProviders, cancellationToken), category)
            ?? await settings.GetAsync(SettingCodes.Files.DefaultProvider, cancellationToken);
        if (configured is not null && providers.TryGet(configured, out var provider))
        {
            return provider;
        }

        // The database provider is capped and meant for chosen categories, so it is the fallback of last resort.
        LogProviderFallback(logger, configured);
        var fallback = providers.Names.FirstOrDefault(n => !string.Equals(n, FileStorageProviderNames.Database, StringComparison.Ordinal)) ?? providers.Names.First();
        return providers.Get(fallback);
    }

    /// <summary>The provider a <c>category=Provider</c> entry names for <paramref name="category"/>, if any.</summary>
    internal static string? ProviderForCategory(IEnumerable<string> entries, string category) =>
        entries
            .Select(e => e.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2 && p[1].Length > 0 && string.Equals(p[0], category, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1])
            .FirstOrDefault();

    /// <summary>
    /// Display name only (the object key never uses it): strips any client path, Windows or Unix, and control characters.
    /// </summary>
    internal static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/'));
        return new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
    }

    private static ValidationResponse<StagedFile> Fail(string message, string field = "file") =>
        ValidationResponse.Failure(new ValidationError(field, message, "file.invalid")).As<StagedFile>();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configured file provider {Provider} is not registered; using the first registered provider")]
    private static partial void LogProviderFallback(ILogger logger, string? provider);
}

/// <summary>Bytes already in storage plus a metadata row staged on the unit of work, waiting for the caller's save.</summary>
internal sealed partial class StagedFile(StoredFile file, IFileStorageProvider provider, ILogger logger)
{
    public StoredFile File { get; } = file;

    public FileUploadResult Result => new(File.Id, File.OriginalFileName, File.ContentType, File.SizeBytes, File.Sha256);

    /// <summary>Saves the unit of work; if that fails, deletes the stored object so no orphan is left behind.</summary>
    public async Task SaveOrDiscardAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        try
        {
            await unitOfWork.SaveAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogOrphanCleanup(logger, File.ObjectKey, provider.Name);
            await provider.DeleteAsync(File.ObjectKey, CancellationToken.None);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Metadata save failed; deleting orphaned object {ObjectKey} from {Provider}")]
    private static partial void LogOrphanCleanup(ILogger logger, string objectKey, string provider);
}
