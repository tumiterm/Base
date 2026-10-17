using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Base.Application.Files;

namespace Base.Infrastructure.Files;

/// <summary>S3-compatible storage: AWS S3, Cloudflare R2 (no egress fees) and MinIO for local development.</summary>
internal sealed class S3FileStorageProvider(IAmazonS3 client, string bucket, TimeProvider time) : IFileStorageProvider
{
    public string Name => FileStorageProviderNames.S3;

    public async Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        await client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = bucket,
                Key = objectKey,
                InputStream = content,
                ContentType = contentType,
                AutoCloseStream = false,
            },
            cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var response = await client.GetObjectAsync(bucket, objectKey, cancellationToken);
        return response.ResponseStream;
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default) =>
        client.DeleteObjectAsync(bucket, objectKey, cancellationToken);

    public async Task<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.GetObjectMetadataAsync(bucket, objectKey, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<Uri?> GetDownloadUrlAsync(string objectKey, string downloadFileName, string contentType, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = time.GetUtcNow().Add(lifetime).UtcDateTime,
        };
        request.ResponseHeaderOverrides.ContentType = contentType;
        request.ResponseHeaderOverrides.ContentDisposition = ContentDisposition.Attachment(downloadFileName);

        return new Uri(await client.GetPreSignedURLAsync(request));
    }
}
