using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Base.Application.Files;

namespace Base.Infrastructure.Files;

internal sealed class AzureBlobFileStorageProvider(BlobContainerClient container, TimeProvider time) : IFileStorageProvider
{
    public string Name => FileStorageProviderNames.AzureBlob;

    public async Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        await container.GetBlobClient(objectKey).UploadAsync(
            content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
            cancellationToken);
    }

    public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default) =>
        container.GetBlobClient(objectKey).OpenReadAsync(cancellationToken: cancellationToken);

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default) =>
        container.GetBlobClient(objectKey).DeleteIfExistsAsync(cancellationToken: cancellationToken);

    public async Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default) =>
        await container.GetBlobClient(objectKey).ExistsAsync(cancellationToken);

    public Task<Uri?> GetDownloadUrlAsync(string objectKey, string downloadFileName, string contentType, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        var blob = container.GetBlobClient(objectKey);
        if (!blob.CanGenerateSasUri)
        {
            return Task.FromResult<Uri?>(null);
        }

        var sas = new BlobSasBuilder(BlobSasPermissions.Read, time.GetUtcNow().Add(lifetime))
        {
            BlobContainerName = container.Name,
            BlobName = objectKey,
            Resource = "b",
            ContentType = contentType,
            ContentDisposition = ContentDisposition.Attachment(downloadFileName),
        };
        return Task.FromResult<Uri?>(blob.GenerateSasUri(sas));
    }
}
