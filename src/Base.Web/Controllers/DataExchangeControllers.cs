using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.DataExchange;
using Base.Domain.DataExchange;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers;

/// <summary>Export history and downloads. Exports start from the buttons on each list (<c>/exports/{code}?format=...</c>).</summary>
[Route("exports")]
public sealed class ExportsController(IExportService exports) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(int page = 1, CancellationToken cancellationToken = default) =>
        View(await exports.HistoryAsync(new PageRequest(page, 25), cancellationToken));

    /// <summary>Runs an export with the list's filters (the query string). Small ones download; large ones are queued.</summary>
    [HttpGet("{code}")]
    public async Task<IActionResult> Run(string code, string? format, CancellationToken cancellationToken)
    {
        var dataFormat = DataFormats.TryParse(format, out var parsed) ? parsed : DataFormat.Xlsx;
        var result = await exports.ExportAsync(code, dataFormat, Filter(Request.Query), cancellationToken);
        if (!result.IsSuccess)
        {
            this.Flash(result);
            return RedirectToAction(nameof(Index));
        }

        if (result.Data!.File is { } file)
        {
            return File(file.Content, file.ContentType, file.FileName);
        }

        this.Flash(ValidationResponse.Success("That's a big export, so it's being prepared in the background. You'll get a notification when it's ready."));
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var result = await exports.DownloadAsync(id, cancellationToken);
        if (!result.IsSuccess)
        {
            return NotFound();
        }

        var download = result.Data!;
        return download.RedirectUrl is not null ? Redirect(download.RedirectUrl.ToString()) : File(download.Content!, download.ContentType, download.FileName);
    }

    /// <summary>The list's query string, minus paging and the format, as the export filter.</summary>
    internal static ExportFilter Filter(IQueryCollection query) =>
        new(query.Where(q => !q.Key.Equals("page", StringComparison.OrdinalIgnoreCase) && !q.Key.Equals("format", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(q => q.Key, q => (string?)q.Value.ToString(), StringComparer.OrdinalIgnoreCase));
}

/// <summary>Upload, preview, confirm and follow imports.</summary>
[Route("imports")]
public sealed class ImportsController(IImportService imports) : Controller
{
    private const long TransportLimitBytes = 500L * 1024 * 1024;

    [HttpGet("")]
    public async Task<IActionResult> Index(int page = 1, CancellationToken cancellationToken = default) =>
        View(new ViewModels.ImportsViewModel(imports.Available, await imports.HistoryAsync(new PageRequest(page, 25), cancellationToken)));

    [HttpGet("{code}/template")]
    public async Task<IActionResult> Template(string code, string? format, CancellationToken cancellationToken)
    {
        var result = await imports.TemplateAsync(code, DataFormats.TryParse(format, out var parsed) ? parsed : DataFormat.Xlsx, cancellationToken);
        return result.IsSuccess ? File(result.Data!.Content, result.Data.ContentType, result.Data.FileName) : NotFound();
    }

    [HttpPost("{code}")]
    [RequestSizeLimit(TransportLimitBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = TransportLimitBytes)]
    public async Task<IActionResult> Upload(string code, IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            this.Flash(ValidationResponse.Failure("Choose a file to import."));
            return RedirectToAction(nameof(Index));
        }

        await using var stream = file.OpenReadStream();
        var result = await imports.UploadAsync(code, stream, file.FileName, cancellationToken);
        if (!result.IsSuccess)
        {
            this.Flash(result);
            return RedirectToAction(nameof(Index));
        }

        return RedirectToAction(nameof(Details), new { id = result.Data!.Id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var result = await imports.GetAsync(id, cancellationToken);
        return result.IsSuccess ? View(result.Data) : NotFound();
    }

    [HttpPost("{id:guid}/confirm")]
    public async Task<IActionResult> Confirm(Guid id, CancellationToken cancellationToken)
    {
        var result = await imports.ConfirmAsync(id, cancellationToken);
        this.Flash(result.IsSuccess ? ValidationResponse.Success("The import is running. You'll get a notification when it finishes.") : result);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet("{id:guid}/errors")]
    public async Task<IActionResult> Errors(Guid id, CancellationToken cancellationToken)
    {
        var result = await imports.ErrorFileAsync(id, cancellationToken);
        return result.IsSuccess ? File(result.Data!.Content, result.Data.ContentType, result.Data.FileName) : NotFound();
    }
}
