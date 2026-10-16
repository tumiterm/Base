using System.Net.Http.Headers;

namespace Base.Infrastructure.Files;

internal static class ContentDisposition
{
    /// <summary>RFC 6266 attachment header with an ASCII fallback and a UTF-8 file name.</summary>
    public static string Attachment(string fileName)
    {
        var header = new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName };
        var ascii = new string(fileName.Select(c => c is >= ' ' and < (char)127 and not '"' and not '\\' ? c : '_').ToArray());
        header.FileName = $"\"{ascii}\"";
        return header.ToString();
    }
}
