namespace Base.Application.Files;

/// <summary>How a browser can show a file without downloading it.</summary>
public enum PreviewKind
{
    /// <summary>No safe in-browser view (Word, Excel, archives...): offer the download.</summary>
    None = 0,
    Pdf = 1,
    Image = 2,

    /// <summary>Plain text, CSV, JSON, XML: shown as text, never interpreted.</summary>
    Text = 3,

    /// <summary>Generated HTML documents, shown in a sandbox with scripts off.</summary>
    Html = 4,
    Video = 5,
    Audio = 6,
}

/// <summary>A file opened for viewing in the page: always a stream through the application, never a redirect.</summary>
public sealed record FilePreview(string FileName, string ContentType, PreviewKind Kind, Stream Content);

/// <summary>
/// Which stored content types can be previewed, and the type they are served as. SVG is never previewed (it can
/// carry script); anything text-like is served as <c>text/plain</c> so the browser can't interpret it.
/// </summary>
public static class FilePreviews
{
    public static PreviewKind KindOf(string? contentType) => Normalize(contentType) switch
    {
        "application/pdf" => PreviewKind.Pdf,
        "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp" or "image/avif" => PreviewKind.Image,
        "text/plain" or "text/csv" or "application/json" or "application/xml" or "text/xml" or "text/markdown" => PreviewKind.Text,
        "text/html" => PreviewKind.Html,
        "video/mp4" or "video/webm" or "video/ogg" => PreviewKind.Video,
        "audio/mpeg" or "audio/mp4" or "audio/ogg" or "audio/wav" or "audio/webm" => PreviewKind.Audio,
        _ => PreviewKind.None,
    };

    public static bool CanPreview(string? contentType) => KindOf(contentType) != PreviewKind.None;

    /// <summary>The Content-Type to serve a preview with.</summary>
    public static string ServedAs(PreviewKind kind, string contentType) => kind switch
    {
        PreviewKind.Text => "text/plain; charset=utf-8",
        PreviewKind.Html => "text/html; charset=utf-8",
        _ => Normalize(contentType),
    };

    private static string Normalize(string? contentType) =>
        (contentType ?? string.Empty).Split(';', 2)[0].Trim().ToLowerInvariant();
}
