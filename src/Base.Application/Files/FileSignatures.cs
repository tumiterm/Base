using System.Collections.Frozen;

namespace Base.Application.Files;

/// <summary>
/// Magic-byte checks: the extension and declared content type are client-controlled, the first bytes are not.
/// Extensions without a signature here (e.g. <c>.txt</c>, <c>.csv</c>) are accepted on extension alone.
/// </summary>
public static class FileSignatures
{
    public const int HeaderLength = 16;

    private static readonly byte[] Pdf = "%PDF-"u8.ToArray();
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Gif = "GIF8"u8.ToArray();
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] Riff = "RIFF"u8.ToArray();
    private static readonly byte[] Webp = "WEBP"u8.ToArray();

    private static readonly FrozenDictionary<string, Func<ReadOnlyMemory<byte>, bool>> Checks =
        new Dictionary<string, Func<ReadOnlyMemory<byte>, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = h => StartsWith(h, Pdf),
            [".png"] = h => StartsWith(h, Png),
            [".jpg"] = h => StartsWith(h, Jpeg),
            [".jpeg"] = h => StartsWith(h, Jpeg),
            [".gif"] = h => StartsWith(h, Gif),
            [".webp"] = h => StartsWith(h, Riff) && h.Length >= 12 && h.Span[8..12].SequenceEqual(Webp),
            [".docx"] = h => StartsWith(h, Zip),
            [".xlsx"] = h => StartsWith(h, Zip),
            [".pptx"] = h => StartsWith(h, Zip),
            [".zip"] = h => StartsWith(h, Zip),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".zip"] = "application/zip",
            [".txt"] = "text/plain",
            [".csv"] = "text/csv",
            [".html"] = "text/html",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the header matches the extension's signature, or the extension has no known signature.</summary>
    public static bool Matches(string extension, ReadOnlySpan<byte> header) =>
        !Checks.TryGetValue(extension, out var check) || check(header.ToArray());

    /// <summary>The content type we serve for an extension. Never trust the client's declared type.</summary>
    public static string ContentTypeFor(string extension) =>
        ContentTypes.GetValueOrDefault(extension, "application/octet-stream");

    private static bool StartsWith(ReadOnlyMemory<byte> header, byte[] signature) =>
        header.Length >= signature.Length && header.Span[..signature.Length].SequenceEqual(signature);
}
