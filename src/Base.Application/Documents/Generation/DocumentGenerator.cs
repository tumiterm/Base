using System.Globalization;
using Base.Application.Common.Results;
using Base.Application.Common.Text;
using Base.Application.Files;
using Base.Application.Settings;
using Base.Domain.Documents;

namespace Base.Application.Documents.Generation;

/// <param name="Data">An object or dictionary with the template's variables.</param>
public sealed record DocumentGenerationRequest(string TemplateCode, string Format, object? Data);

/// <param name="AddToDocumentId">Adds the output as a new version of this (draft) document instead of creating one.</param>
/// <param name="Finalize">Marks a new document final straight away (e.g. an issued letter).</param>
public sealed record GenerateDocumentRequest(
    string TemplateCode,
    string Format,
    object? Data,
    string? Title = null,
    IReadOnlyList<string>? Tags = null,
    DocumentLink? LinkedTo = null,
    Guid? AddToDocumentId = null,
    bool Finalize = false,
    string? Comment = null);

public sealed record GeneratedFile(string FileName, string ContentType, byte[] Content);

/// <summary>Produces documents from <see cref="DocumentTemplateCatalog"/> templates, in any format a renderer is registered for.</summary>
public interface IDocumentGenerator
{
    /// <summary>The formats that can be produced, e.g. <c>html</c>, <c>pdf</c>.</summary>
    IReadOnlyCollection<string> Formats { get; }

    /// <summary>Renders without storing anything (preview, one-off download).</summary>
    Task<ValidationResponse<GeneratedFile>> RenderAsync(DocumentGenerationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Renders and stores the result as a document (or a new version of one), through the same checks as uploads.</summary>
    Task<ValidationResponse<DocumentDetails>> GenerateAsync(GenerateDocumentRequest request, CancellationToken cancellationToken = default);
}

internal sealed class DocumentGenerator(
    IEnumerable<IDocumentRenderer> renderers,
    DocumentService documents,
    IApplicationSettingService settings,
    TimeProvider time) : IDocumentGenerator
{
    // Later registrations win, so a project can replace a built-in renderer by registering its own.
    private readonly Dictionary<string, IDocumentRenderer> _renderers = renderers
        .GroupBy(r => r.Format, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Formats => _renderers.Keys.Order(StringComparer.Ordinal).ToList();

    public async Task<ValidationResponse<GeneratedFile>> RenderAsync(DocumentGenerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rendered = await RenderCoreAsync(request.TemplateCode, request.Format, request.Data, null, cancellationToken);
        return rendered.IsSuccess ? rendered.Data!.File : rendered.As<GeneratedFile>();
    }

    public async Task<ValidationResponse<DocumentDetails>> GenerateAsync(GenerateDocumentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rendered = await RenderCoreAsync(request.TemplateCode, request.Format, request.Data, request.Title, cancellationToken);
        if (!rendered.IsSuccess)
        {
            return rendered.As<DocumentDetails>();
        }

        var (template, renderer, file) = rendered.Data!;
        await using var content = new MemoryStream(file.Content, writable: false);
        var upload = new DocumentUpload(content, file.FileName);
        string[] extensions = [renderer.FileExtension];

        if (request.AddToDocumentId is { } documentId)
        {
            return await documents.AddVersionCoreAsync(documentId, upload, request.Comment, DocumentSource.Generated, template.Code, extensions, cancellationToken);
        }

        var create = new CreateDocumentRequest(
            string.IsNullOrWhiteSpace(request.Title) ? template.Name : request.Title, template.DocumentType, upload, null, request.Tags, request.LinkedTo, request.Comment);
        return await documents.CreateCoreAsync(create, DocumentSource.Generated, template.Code, extensions, request.Finalize, cancellationToken);
    }

    private async Task<ValidationResponse<Rendered>> RenderCoreAsync(string templateCode, string format, object? data, string? title, CancellationToken cancellationToken)
    {
        var template = DocumentTemplateCatalog.Find(templateCode);
        var html = template is null ? null : DocumentTemplateFiles.Find(template.Code);
        if (template is null || html is null)
        {
            return Invalid("TemplateCode", $"Unknown document template '{templateCode}'.");
        }

        if (!_renderers.TryGetValue(format ?? string.Empty, out var renderer))
        {
            return Invalid("Format", $"Documents can't be produced as '{format}' here. Available: {string.Join(", ", Formats)}.");
        }

        // Case-insensitive: JSON callers send camelCase ("recipientName") for the template's "RecipientName".
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in TemplateValues.From(data))
        {
            values[key] = value;
        }

        values[DocumentTemplateCatalog.AppNameVariable] = await settings.GetAsync(SettingCodes.General.ApplicationName, cancellationToken);
        values[DocumentTemplateCatalog.TodayVariable] = await TodayAsync(cancellationToken);
        var missing = template.Variables.Where(v => !values.ContainsKey(v)).ToList();
        if (missing.Count > 0)
        {
            return Invalid("Data", $"Template {template.Code} needs: {string.Join(", ", missing)}.");
        }

        using var output = new MemoryStream();
        try
        {
            await renderer.RenderAsync(new DocumentRenderContext(template, TemplateRenderer.Render(html, values, html: true), values, data), output, cancellationToken);
        }
        catch (DocumentRenderException exception)
        {
            return ValidationResponse.Failure(exception.Message, "document.render").As<Rendered>();
        }

        var name = SafeFileName(string.IsNullOrWhiteSpace(title) ? template.Name : title);
        var fileName = (name.Length == 0 ? "document" : name) + renderer.FileExtension;
        return new Rendered(template, renderer, new GeneratedFile(fileName, FileSignatures.ContentTypeFor(renderer.FileExtension), output.ToArray()));
    }

    /// <summary>The title as a file name that is valid on every OS (Windows has the strictest rules).</summary>
    internal static string SafeFileName(string title)
    {
        var safe = new string(title.Trim().Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c, StringComparison.Ordinal) ? '_' : c).ToArray()).Trim(' ', '.');
        return safe.Length > 200 ? safe[..200] : safe;
    }

    private async Task<string> TodayAsync(CancellationToken cancellationToken)
    {
        var zoneId = await settings.GetAsync(SettingCodes.General.DisplayTimeZone, cancellationToken);
        var zone = zoneId is not null && TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var found) ? found : TimeZoneInfo.Utc;
        return TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
    }

    private static ValidationResponse<Rendered> Invalid(string field, string message) =>
        ValidationResponse.Failure(new ValidationError(field, message, "document.invalid")).As<Rendered>();

    private sealed record Rendered(DocumentTemplateDefinition Template, IDocumentRenderer Renderer, GeneratedFile File);
}
