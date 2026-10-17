using System.Globalization;
using Base.Application.Files;
using Base.Domain.Documents;

namespace Base.Web.Formatting;

/// <summary>How files and documents are shown: sizes, type icons, status badges.</summary>
public static class FileDisplay
{
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
    };

    public static string Icon(string? contentType, string? fileName = null)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return FilePreviews.KindOf(contentType) switch
        {
            PreviewKind.Pdf => "file-text",
            PreviewKind.Image => "file-image",
            PreviewKind.Video => "file-video",
            PreviewKind.Audio => "file-audio",
            PreviewKind.Html => "file-code",
            PreviewKind.Text when extension is ".json" or ".xml" => "file-code",
            PreviewKind.Text when string.Equals(extension, ".csv", StringComparison.Ordinal) => "file-spreadsheet",
            PreviewKind.Text => "file-text",
            _ => extension switch
            {
                ".xlsx" or ".xls" or ".ods" => "file-spreadsheet",
                ".zip" or ".7z" or ".gz" or ".rar" => "file-archive",
                ".docx" or ".doc" or ".odt" or ".rtf" => "file-text",
                _ => "file",
            },
        };
    }

    /// <summary>The <c>.file-tile</c> colour class for the file's kind.</summary>
    public static string TileClass(string? contentType, string? fileName) => Icon(contentType, fileName) switch
    {
        "file-text" when FilePreviews.KindOf(contentType) == PreviewKind.Pdf => "is-pdf",
        "file-image" => "is-image",
        "file-video" or "file-audio" => "is-media",
        "file-spreadsheet" => "is-sheet",
        "file-code" => "is-code",
        _ => string.Empty,
    };

    /// <summary>"PDF", "Word document", "PNG image": what the file is, in words.</summary>
    public static string Describe(string? contentType, string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "pdf" => "PDF",
            "docx" or "doc" => "Word document",
            "xlsx" or "xls" => "Excel workbook",
            "pptx" or "ppt" => "PowerPoint deck",
            "csv" => "CSV file",
            "txt" => "Text file",
            "html" or "htm" => "Web page",
            "png" or "jpg" or "jpeg" or "gif" or "webp" => extension.ToUpperInvariant().Replace("JPEG", "JPG", StringComparison.Ordinal) + " image",
            "" => contentType ?? "File",
            _ => extension.ToUpperInvariant() + " file",
        };
    }

    public static string Tone(DocumentStatus status) => status switch
    {
        DocumentStatus.Final => "success",
        DocumentStatus.Archived => "secondary",
        _ => "warning",
    };

    public static string Status(DocumentStatus status) => status switch
    {
        DocumentStatus.Final => "Final",
        DocumentStatus.Archived => "Archived",
        _ => "Draft",
    };
}
