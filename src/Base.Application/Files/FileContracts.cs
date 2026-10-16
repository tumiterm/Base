namespace Base.Application.Files;

/// <param name="Category">What the file is for (<c>avatar</c>, <c>invoice</c>, ...). Lower-case letters, digits and dashes.</param>
public sealed record FileUploadRequest(Stream Content, string FileName, string? ContentType, string Category, DateTimeOffset? ExpiresAt = null);

public sealed record FileUploadResult(Guid FileId, string FileName, string ContentType, long SizeBytes, string Sha256);

public enum FileAccessPurpose
{
    Read = 0,
    Delete = 1,
}

/// <summary>Either a pre-signed <see cref="RedirectUrl"/> or a <see cref="Content"/> stream to send.</summary>
public sealed record FileDownload(string FileName, string ContentType, Uri? RedirectUrl, Stream? Content);
