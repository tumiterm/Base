using Base.Application.Common.Results;
using Base.Application.Documents;
using Base.Application.Documents.Generation;
using Base.Application.Subscriptions;
using Base.Web.Controllers.Api;
using Base.Web.Results;
using Base.Web.Subscriptions;
using Base.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers;

/// <summary>Document pages. What each user may do is decided per document by <see cref="IDocumentAccessPolicy"/>.</summary>
[RequiresFeature(FeatureCodes.Documents)]
[Route("documents")]
public sealed class DocumentsController(IDocumentService documents, IDocumentGenerator generator, IDocumentAccessPolicy policy) : Controller
{
    private const long TransportLimitBytes = 500L * 1024 * 1024;

    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] DocumentFilterViewModel filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return View(new DocumentListViewModel(await documents.SearchAsync(filter.ToQuery(), cancellationToken), filter, policy.CanCreate));
    }

    [HttpPost("")]
    [RequestSizeLimit(TransportLimitBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = TransportLimitBytes)]
    public async Task<IActionResult> Create([FromForm] CreateDocumentForm form, string? returnUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.File is null)
        {
            this.Flash(ValidationResponse.Failure("Choose a file to upload."));
            return BackTo(returnUrl);
        }

        await using var stream = form.File.OpenReadStream();
        var result = await documents.CreateAsync(form.ToRequest(new DocumentUpload(stream, form.File.FileName)), cancellationToken);
        if (!result.IsSuccess)
        {
            this.Flash(result);
            return BackTo(returnUrl);
        }

        this.Flash(ValidationResponse.Success($"Uploaded \"{result.Data!.Title}\"."));
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Details), new { id = result.Data.Id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var result = await documents.GetAsync(id, cancellationToken);
        return result.IsSuccess ? View(result.Data) : NotFound();
    }

    [HttpPost("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, string title, string? description, string? tags, CancellationToken cancellationToken)
    {
        var result = await documents.UpdateAsync(id, new UpdateDocumentRequest(title, description, CreateDocumentForm.SplitTags(tags)), cancellationToken);
        this.Flash(result.IsSuccess ? ValidationResponse.Success("Details saved.") : result);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/versions")]
    [RequestSizeLimit(TransportLimitBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = TransportLimitBytes)]
    public async Task<IActionResult> AddVersion(Guid id, IFormFile? file, string? comment, CancellationToken cancellationToken)
    {
        if (file is null)
        {
            this.Flash(ValidationResponse.Failure("Choose a file to upload."));
            return RedirectToAction(nameof(Details), new { id });
        }

        await using var stream = file.OpenReadStream();
        var result = await documents.AddVersionAsync(id, new DocumentUpload(stream, file.FileName), comment, cancellationToken);
        this.Flash(result.IsSuccess ? ValidationResponse.Success($"Version {result.Data!.CurrentVersion} added.") : result);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/{change:regex(^(finalize|reopen|archive|restore)$)}")]
    public async Task<IActionResult> ChangeStatus(Guid id, string change, CancellationToken cancellationToken)
    {
        var result = change switch
        {
            "finalize" => await documents.FinalizeAsync(id, cancellationToken),
            "reopen" => await documents.ReopenAsync(id, cancellationToken),
            "archive" => await documents.ArchiveAsync(id, cancellationToken),
            _ => await documents.RestoreAsync(id, cancellationToken),
        };
        this.Flash(result);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/delete")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await documents.DeleteAsync(id, cancellationToken);
        this.Flash(result);
        return result.IsSuccess ? RedirectToAction(nameof(Index)) : RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, int? version, CancellationToken cancellationToken)
    {
        var result = await documents.DownloadAsync(id, version, cancellationToken);
        if (!result.IsSuccess)
        {
            return NotFound();
        }

        var download = result.Data!;
        return download.RedirectUrl is not null
            ? Redirect(download.RedirectUrl.ToString())
            : File(download.Content!, download.ContentType, download.FileName, enableRangeProcessing: true);
    }

    [HttpGet("generate")]
    public IActionResult Generate(string? template) =>
        View(Model(DocumentTemplateCatalog.Find(template), new Dictionary<string, string?>(StringComparer.Ordinal), null, DefaultFormat(), finalize: false));

    /// <summary>Generates and stores the document, or with <c>preview</c> just downloads the rendered file.</summary>
    [HttpPost("generate")]
    public async Task<IActionResult> Generate(string template, string format, string? title, bool finalize, bool preview, [FromForm] IDictionary<string, string?> values, CancellationToken cancellationToken)
    {
        var definition = DocumentTemplateCatalog.Find(template);
        if (definition is null)
        {
            return NotFound();
        }

        var data = definition.Variables.ToDictionary(v => v, v => values.TryGetValue(v, out var value) ? value : null, StringComparer.Ordinal);
        if (preview)
        {
            var rendered = await generator.RenderAsync(new DocumentGenerationRequest(definition.Code, format, data), cancellationToken);
            if (rendered.IsSuccess)
            {
                return File(rendered.Data!.Content, rendered.Data.ContentType, rendered.Data.FileName);
            }

            rendered.AddToModelState(ModelState);
            return View(Model(definition, data, title, format, finalize));
        }

        var result = await generator.GenerateAsync(new GenerateDocumentRequest(definition.Code, format, data, title, Finalize: finalize), cancellationToken);
        if (!result.IsSuccess)
        {
            result.AddToModelState(ModelState);
            return View(Model(definition, data, title, format, finalize));
        }

        this.Flash(ValidationResponse.Success($"Generated \"{result.Data!.Title}\"."));
        return RedirectToAction(nameof(Details), new { id = result.Data.Id });
    }

    private GenerateDocumentViewModel Model(DocumentTemplateDefinition? selected, IReadOnlyDictionary<string, string?> values, string? title, string format, bool finalize) =>
        new(DocumentTemplateCatalog.All, generator.Formats, selected, values, title, format, finalize);

    private string DefaultFormat() => generator.Formats.Contains(DocumentFormats.Pdf, StringComparer.Ordinal) ? DocumentFormats.Pdf : DocumentFormats.Html;

    private IActionResult BackTo(string? returnUrl) => Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Index));
}
