using Base.Application.Common.Persistence;
using Base.Application.DataExchange;
using Base.Domain.DataExchange;
using Base.Web.Results;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers.Api;

/// <summary>Exports over the API. Named apart from the page controllers so tag helpers never link pages here.</summary>
[ApiController]
[Route("api/exports")]
public sealed class ExportsApiController(IExportService exports) : ControllerBase
{
    [HttpGet]
    public IReadOnlyList<ExporterInfo> Exporters() => exports.Available;

    [HttpGet("history")]
    public Task<PagedResult<ExportJobView>> History(int page = 1, CancellationToken cancellationToken = default) =>
        exports.HistoryAsync(new PageRequest(page, 25), cancellationToken);

    /// <summary>The file, or 202 Accepted with the job id when the export is large enough to run in the background.</summary>
    [HttpGet("{code}")]
    public async Task<IActionResult> Run(string code, string? format, CancellationToken cancellationToken)
    {
        var result = await exports.ExportAsync(code, DataFormats.TryParse(format, out var parsed) ? parsed : DataFormat.Csv, ExportsController.Filter(Request.Query), cancellationToken);
        if (!result.IsSuccess)
        {
            return result.ToActionResult(this);
        }

        return result.Data!.File is { } file
            ? File(file.Content, file.ContentType, file.FileName)
            : Accepted(new { jobId = result.Data.JobId });
    }

    [HttpGet("jobs/{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var result = await exports.DownloadAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            return result.ToActionResult(this);
        }

        var download = result.Data!;
        return download.RedirectUrl is not null ? Redirect(download.RedirectUrl.ToString()) : File(download.Content!, download.ContentType, download.FileName);
    }
}

[ApiController]
[Route("api/imports")]
public sealed class ImportsApiController(IImportService imports) : ControllerBase
{
    private const long TransportLimitBytes = 500L * 1024 * 1024;

    [HttpGet]
    public IReadOnlyList<ImporterInfo> Importers() => imports.Available;

    [HttpGet("history")]
    public Task<PagedResult<ImportJobView>> History(int page = 1, CancellationToken cancellationToken = default) =>
        imports.HistoryAsync(new PageRequest(page, 25), cancellationToken);

    [HttpGet("{code}/template")]
    public async Task<IActionResult> Template(string code, string? format, CancellationToken cancellationToken)
    {
        var result = await imports.TemplateAsync(code, DataFormats.TryParse(format, out var parsed) ? parsed : DataFormat.Xlsx, cancellationToken);
        return result.IsSuccess ? File(result.Data!.Content, result.Data.ContentType, result.Data.FileName) : result.ToActionResult(this);
    }

    /// <summary>Uploads a file and returns the dry-run result. Nothing is applied until it is confirmed.</summary>
    [HttpPost("{code}")]
    [RequestSizeLimit(TransportLimitBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = TransportLimitBytes)]
    public async Task<IActionResult> Upload(string code, IFormFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        await using var stream = file.OpenReadStream();
        var result = await imports.UploadAsync(code, stream, file.FileName, cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Data!.Id }, result.Data) : result.ToActionResult(this);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) => (await imports.GetAsync(id, cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid id, CancellationToken cancellationToken) => (await imports.ConfirmAsync(id, cancellationToken)).ToActionResult(this);

    [HttpGet("{id:guid}/errors")]
    public async Task<IActionResult> Errors(Guid id, CancellationToken cancellationToken)
    {
        var result = await imports.ErrorFileAsync(id, cancellationToken);
        return result.IsSuccess ? File(result.Data!.Content, result.Data.ContentType, result.Data.FileName) : result.ToActionResult(this);
    }
}
