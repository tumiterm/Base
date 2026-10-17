using System.ComponentModel.DataAnnotations;
using System.Text;
using Base.Application.Documents.Generation;
using Microsoft.Extensions.Logging;

namespace Base.Infrastructure.Documents;

/// <summary>A Gotenberg server (https://gotenberg.dev, MIT): HTML to PDF with headless Chromium, over HTTP.</summary>
public sealed class GotenbergOptions
{
    public const string SectionName = "Documents:Pdf:Gotenberg";

    /// <summary>e.g. <c>http://gotenberg:3000/</c>. The renderer is registered only when this is set.</summary>
    public Uri? BaseUrl { get; set; }

    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Produces PDFs from the filled-in HTML template via Gotenberg, so the base needs no licensed PDF engine. Run it next
/// to the app (see docker-compose, profile <c>pdf</c>); it should not be reachable from the internet.
/// </summary>
internal sealed partial class GotenbergPdfRenderer(IHttpClientFactory httpClients, ILogger<GotenbergPdfRenderer> logger) : IDocumentRenderer
{
    public const string HttpClientName = "gotenberg";
    private const int MaxLoggedBody = 500;

    public string Format => DocumentFormats.Pdf;

    public string FileExtension => ".pdf";

    public async Task RenderAsync(DocumentRenderContext context, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);

        // Gotenberg's Chromium route takes the page as a file that must be called index.html.
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(context.Html, Encoding.UTF8, "text/html"), "files", "index.html");
        form.Add(new StringContent("true"), "printBackground");
        form.Add(new StringContent("true"), "preferCssPageSize");

        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).PostAsync(new Uri("forms/chromium/convert/html", UriKind.Relative), form, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                LogFailed(logger, null, context.Template.Code, (int)response.StatusCode, body.Length > MaxLoggedBody ? body[..MaxLoggedBody] : body);
                throw new DocumentRenderException("The PDF could not be produced right now. Please try again later.");
            }

            await response.Content.CopyToAsync(output, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogFailed(logger, exception, context.Template.Code, 0, exception.Message);
            throw new DocumentRenderException("The PDF service is unavailable right now. Please try again later.", exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Gotenberg could not render {Template} (HTTP {Status}): {Detail}")]
    private static partial void LogFailed(ILogger logger, Exception? exception, string template, int status, string detail);
}
