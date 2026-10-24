using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Documents;
using Base.Application.Documents.Generation;
using Base.Application.Files;
using Base.Application.Subscriptions;
using Base.Domain.Documents;
using Base.Web.Results;
using Base.Web.Subscriptions;
using Microsoft.AspNetCore.Mvc;

namespace Base.Web.Controllers.Api;

/// <summary>
/// Versioned documents. Access is decided per document by <see cref="IDocumentAccessPolicy"/> (a document you may not
/// read is a 404). Named apart from the page controller so tag helpers never link pages to the API.
/// </summary>
[ApiController]
[RequiresFeature(FeatureCodes.Documents)]
[Route("api/documents")]
public sealed class DocumentsApiController(IDocumentService documents, IDocumentGenerator generator) : ControllerBase
{
    // Transport limit only; the real limit is the Files.MaxUploadSizeMb setting, enforced while streaming.
    private const long TransportLimitBytes = 500L * 1024 * 1024;

    [HttpGet]
    public async Task<PagedResult<DocumentSummary>> Search([FromQuery] DocumentSearchParameters query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await documents.SearchAsync(query.ToQuery(), cancellationToken);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        (await documents.GetAsync(id, cancellationToken)).ToActionResult(this);

    [HttpPost]
    [RequestSizeLimit(TransportLimitBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = TransportLimitBytes)]
    public async Task<IActionResult> Create([FromForm] CreateDocumentForm form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(form);
        await using var stream = form.File.OpenReadStream();
        var result = await documents.CreateAsync(form.ToRequest(new DocumentUpload(stream, form.File.FileName)), cancellationToken);
        return Created(result);
    }

    [HttpPost("{id:guid}/versions")]
    [RequestSizeLimit(TransportLimitBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = TransportLimitBytes)]
    public async Task<IActionResult> AddVersion(Guid id, IFormFile file, [FromForm] string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        await using var stream = file.OpenReadStream();
        return (await documents.AddVersionAsync(id, new DocumentUpload(stream, file.FileName), comment, cancellationToken)).ToActionResult(this);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateDocumentRequest request, CancellationToken cancellationToken) =>
        (await documents.UpdateAsync(id, request, cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/finalize")]
    public async Task<IActionResult> Finalize(Guid id, CancellationToken cancellationToken) => (await documents.FinalizeAsync(id, cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/reopen")]
    public async Task<IActionResult> Reopen(Guid id, CancellationToken cancellationToken) => (await documents.ReopenAsync(id, cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/archive")]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken) => (await documents.ArchiveAsync(id, cancellationToken)).ToActionResult(this);

    [HttpPost("{id:guid}/restore")]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken) => (await documents.RestoreAsync(id, cancellationToken)).ToActionResult(this);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken) => (await documents.DeleteAsync(id, cancellationToken)).ToActionResult(this);

    /// <summary>The latest version, or <paramref name="version"/>. Redirects to a pre-signed URL when the provider has one.</summary>
    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, int? version, CancellationToken cancellationToken) =>
        Send(await documents.DownloadAsync(id, version, cancellationToken));

    /// <summary>The templates that can be generated and the formats available on this installation.</summary>
    [HttpGet("templates")]
    [RequiresFeature(FeatureCodes.DocumentGeneration)]
    public object Templates() => new { Formats = generator.Formats, Templates = DocumentTemplateCatalog.ForPeople, DocumentTemplateCatalog.BuiltInVariables };

    /// <summary>Renders and stores a document (or a new version of one).</summary>
    [HttpPost("generate")]
    [RequiresFeature(FeatureCodes.DocumentGeneration)]
    public async Task<IActionResult> Generate(GenerateDocumentBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Created(await generator.GenerateAsync(body.ToRequest(), cancellationToken));
    }

    /// <summary>Renders without storing anything, for previews.</summary>
    [HttpPost("render")]
    public async Task<IActionResult> Render(RenderDocumentBody body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var result = await generator.RenderAsync(new DocumentGenerationRequest(body.TemplateCode, body.Format, body.Data), cancellationToken);
        return result.IsSuccess ? File(result.Data!.Content, result.Data.ContentType, result.Data.FileName) : result.ToActionResult(this);
    }

    private IActionResult Created(ValidationResponse<DocumentDetails> result) =>
        result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Data!.Id }, result.Data) : result.ToActionResult(this);

    private IActionResult Send(ValidationResponse<FileDownload> result)
    {
        if (!result.IsSuccess)
        {
            return result.ToActionResult(this);
        }

        var download = result.Data!;
        return download.RedirectUrl is not null
            ? Redirect(download.RedirectUrl.ToString())
            : File(download.Content!, download.ContentType, download.FileName, enableRangeProcessing: true);
    }
}

public sealed class DocumentSearchParameters
{
    public string? Search { get; set; }

    public string? Type { get; set; }

    public string? Tag { get; set; }

    public DocumentStatus? Status { get; set; }

    public string? LinkType { get; set; }

    public string? LinkId { get; set; }

    public bool IncludeArchived { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = PageRequest.DefaultPageSize;

    public DocumentQuery ToQuery() => new(
        Search, Type, Tag, Status, string.IsNullOrWhiteSpace(LinkType) || string.IsNullOrWhiteSpace(LinkId) ? null : new DocumentLink(LinkType, LinkId), IncludeArchived, Page, PageSize);
}

public sealed class CreateDocumentForm
{
    public IFormFile File { get; set; } = null!;

    public string Title { get; set; } = string.Empty;

    public string Type { get; set; } = DocumentTypes.General;

    public string? Description { get; set; }

    /// <summary>Comma-separated.</summary>
    public string? Tags { get; set; }

    public string? LinkType { get; set; }

    public string? LinkId { get; set; }

    public string? Comment { get; set; }

    public CreateDocumentRequest ToRequest(DocumentUpload upload) => new(
        Title, Type, upload, Description, SplitTags(Tags),
        string.IsNullOrWhiteSpace(LinkType) && string.IsNullOrWhiteSpace(LinkId) ? null : new DocumentLink(LinkType ?? string.Empty, LinkId ?? string.Empty), Comment);

    public static IReadOnlyList<string> SplitTags(string? tags) =>
        (tags ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

public sealed record RenderDocumentBody(string TemplateCode, string Format, IReadOnlyDictionary<string, string?>? Data);

public sealed record GenerateDocumentBody(
    string TemplateCode,
    string Format,
    IReadOnlyDictionary<string, string?>? Data,
    string? Title = null,
    IReadOnlyList<string>? Tags = null,
    DocumentLink? LinkedTo = null,
    Guid? AddToDocumentId = null,
    bool Finalize = false,
    string? Comment = null)
{
    public GenerateDocumentRequest ToRequest() => new(TemplateCode, Format, Data, Title, Tags, LinkedTo, AddToDocumentId, Finalize, Comment);
}
