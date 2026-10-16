using Base.Application.Common.Results;

namespace Base.Application.Files;

/// <summary>The only entry point business code uses for files.</summary>
public interface IFileService
{
    Task<ValidationResponse<FileUploadResult>> UploadAsync(FileUploadRequest request, CancellationToken cancellationToken = default);

    Task<ValidationResponse<FileDownload>> GetDownloadAsync(Guid fileId, CancellationToken cancellationToken = default);

    Task<ValidationResponse> DeleteAsync(Guid fileId, CancellationToken cancellationToken = default);
}
