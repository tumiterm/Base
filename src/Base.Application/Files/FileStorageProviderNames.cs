namespace Base.Application.Files;

/// <summary>Names of the built-in storage providers, as recorded on each file and used in the <c>Files.*</c> settings.</summary>
public static class FileStorageProviderNames
{
    public const string LocalDisk = "LocalDisk";
    public const string S3 = "S3";
    public const string AzureBlob = "AzureBlob";

    /// <summary>Bytes in SQL Server, capped by the <c>Files.Database.*</c> settings. For small files only.</summary>
    public const string Database = "Database";
}

/// <summary>A provider refused to store a file because of a size or quota limit. The message is safe to show.</summary>
public sealed class FileStorageLimitException(string message) : Exception(message);
