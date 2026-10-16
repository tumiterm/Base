namespace Base.Infrastructure.Files;

/// <summary>
/// Storage provider configuration. A provider is registered only when its section is configured.
/// Credentials belong in user-secrets / environment variables, never in committed appsettings.
/// </summary>
public sealed class FileStorageOptions
{
    public const string SectionName = "FileStorage";

    public LocalDiskOptions? LocalDisk { get; set; }

    public S3Options? S3 { get; set; }

    public AzureBlobOptions? AzureBlob { get; set; }

    public sealed class LocalDiskOptions
    {
        /// <summary>Absolute, or relative to the content root. For development and tests only.</summary>
        public string RootPath { get; set; } = ".data/files";
    }

    /// <summary>Any S3-compatible store: AWS S3, Cloudflare R2, MinIO.</summary>
    public sealed class S3Options
    {
        public string Bucket { get; set; } = string.Empty;

        /// <summary>Custom endpoint for R2 (<c>https://&lt;account&gt;.r2.cloudflarestorage.com</c>) or MinIO. Empty for AWS.</summary>
        public string? ServiceUrl { get; set; }

        public string Region { get; set; } = "auto";

        public string? AccessKey { get; set; }

        public string? SecretKey { get; set; }

        /// <summary>Required by MinIO and most self-hosted stores.</summary>
        public bool ForcePathStyle { get; set; }
    }

    public sealed class AzureBlobOptions
    {
        public string ConnectionString { get; set; } = string.Empty;

        public string Container { get; set; } = "files";
    }
}
