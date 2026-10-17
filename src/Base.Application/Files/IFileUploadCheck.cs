using Base.Application.Common.Results;

namespace Base.Application.Files;

/// <summary>
/// An extra check every upload passes before it is stored, after the built-in ones (extension, size, content
/// signature). Register as many as needed (malware scanning, image dimensions, PDF without scripts...); they run in
/// <see cref="Order"/>. The content stream is positioned at the start and must not be disposed.
/// </summary>
public interface IFileUploadCheck
{
    int Order => 0;

    /// <summary>Returns a failure (shown to the user) to reject the file.</summary>
    Task<ValidationResponse> CheckAsync(FileUploadCheckContext context, CancellationToken cancellationToken = default);
}

/// <summary>What a <see cref="IFileUploadCheck"/> sees. <see cref="ContentType"/> comes from the signature, not the client.</summary>
public sealed record FileUploadCheckContext(string FileName, string Extension, string ContentType, string Category, long SizeBytes, string Sha256, Stream Content);
