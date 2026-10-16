using Base.Application.Files;
using Base.Application.Security;
using Base.Web.Results;
using Base.Web.Security;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers.Api;

[ApiController]
[Route("api/files")]
public sealed class FilesController(IFileService files) : ControllerBase
{
    // Transport limit only; the real limit is the Files.MaxUploadSizeMb setting, enforced while streaming.
    private const long TransportLimitBytes = 500L * 1024 * 1024;

    [HttpPost]
    [HasPermission(PermissionCodes.Files.Upload)]
    [RequestSizeLimit(TransportLimitBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = TransportLimitBytes)]
    public async Task<IActionResult> Upload(IFormFile file, [FromForm] string category, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        await using var stream = file.OpenReadStream();
        var result = await files.UploadAsync(new FileUploadRequest(stream, file.FileName, file.ContentType, category), cancellationToken);
        return result.IsSuccess
            ? CreatedAtAction(nameof(Download), new { id = result.Data!.FileId }, result.Data)
            : result.ToActionResult(this);
    }

    /// <summary>Redirects to a short-lived pre-signed URL when the provider supports it, otherwise streams.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var result = await files.GetDownloadAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.ToActionResult(this);
        }

        var download = result.Data!;
        return download.RedirectUrl is not null
            ? Redirect(download.RedirectUrl.ToString())
            : File(download.Content!, download.ContentType, download.FileName, enableRangeProcessing: true);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) =>
        (await files.DeleteAsync(id, cancellationToken)).ToActionResult(this);
}
