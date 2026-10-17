using System.Text;

namespace Base.Application.Documents.Generation;

/// <summary>What a renderer gets: the filled-in HTML, and the values and raw data for engines that build documents in code.</summary>
public sealed record DocumentRenderContext(DocumentTemplateDefinition Template, string Html, IReadOnlyDictionary<string, string?> Values, object? Data);

/// <summary>
/// Turns a template into one output format. Register one per format (the last registration wins): the built-in
/// HTML renderer, Gotenberg for PDF, or your own (QuestPDF, a Word engine...). The base ships no licensed engine.
/// </summary>
public interface IDocumentRenderer
{
    /// <summary>A <see cref="DocumentFormats"/> value, lower case.</summary>
    string Format { get; }

    /// <summary>The produced file's extension, e.g. <c>.pdf</c>.</summary>
    string FileExtension { get; }

    /// <summary>Writes the document to <paramref name="output"/>. Throw <see cref="DocumentRenderException"/> for failures users should see.</summary>
    Task RenderAsync(DocumentRenderContext context, Stream output, CancellationToken cancellationToken = default);
}

/// <summary>Rendering failed; the message is safe to show (put details in the log).</summary>
public sealed class DocumentRenderException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>The filled-in template as an HTML file. Always available, and the basis for HTML-to-PDF renderers.</summary>
internal sealed class HtmlDocumentRenderer : IDocumentRenderer
{
    public string Format => DocumentFormats.Html;

    public string FileExtension => ".html";

    public async Task RenderAsync(DocumentRenderContext context, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);
        await output.WriteAsync(Encoding.UTF8.GetBytes(context.Html), cancellationToken);
    }
}
