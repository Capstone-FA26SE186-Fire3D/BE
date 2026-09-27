using Amazon.S3;
using Amazon.S3.Model;
using Fire3D.Application.Storage;
using Microsoft.Extensions.Configuration;

namespace Fire3D.Infrastructure.Storage;

public class S3StorageService(IAmazonS3 s3Client, IConfiguration configuration) : IStorageService
{
    private readonly string _bucketName = ResolveBucketName(configuration);

    internal static string ResolveBucketName(IConfiguration configuration) =>
        string.IsNullOrWhiteSpace(configuration["AWS:BucketName"])
            ? Environment.GetEnvironmentVariable("S3_BUCKET_NAME") ?? "fire3d-uploads"
            : configuration["AWS:BucketName"]!;

    public async Task<string> GeneratePresignedUploadUrlAsync(string objectKey, string mimeType, TimeSpan expiration, CancellationToken ct)
    {
        var request = new Amazon.S3.Model.GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.Add(expiration),
            ContentType = mimeType
        };
        
        return await s3Client.GetPreSignedURLAsync(request);
    }

    public async Task<bool> VerifyObjectExistsAsync(string objectKey, long expectedSizeBytes, CancellationToken ct)
    {
        try
        {
            var request = new Amazon.S3.Model.GetObjectMetadataRequest
            {
                BucketName = _bucketName,
                Key = objectKey
            };
            
            var response = await s3Client.GetObjectMetadataAsync(request, ct);
            return response.ContentLength == expectedSizeBytes;
        }
        catch (Amazon.S3.AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<StorageObjectMetadata?> GetObjectMetadataAsync(string objectKey, CancellationToken ct)
    {
        try
        {
            var response = await s3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucketName, Key = objectKey }, ct);
            return new(response.ContentLength, response.Headers.ContentType, response.ETag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<byte[]?> ReadObjectPrefixAsync(string objectKey, int length, string expectedETag, CancellationToken ct)
    {
        try
        {
            using var response = await s3Client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucketName, Key = objectKey, ByteRange = new ByteRange(0, Math.Max(0, length - 1)),
                EtagToMatch = expectedETag
            }, ct);
            var buffer = new byte[length];
            var read = await response.ResponseStream.ReadAsync(buffer.AsMemory(), ct);
            return buffer[..read];
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.PreconditionFailed) { return null; }
    }

    public async Task<bool> CopyObjectIfUnchangedAsync(string sourceKey, string sourceETag, string destinationKey, string contentType, CancellationToken ct)
    {
        try
        {
            await s3Client.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = _bucketName, SourceKey = sourceKey, ETagToMatch = sourceETag,
                DestinationBucket = _bucketName, DestinationKey = destinationKey, MetadataDirective = S3MetadataDirective.REPLACE,
                ContentType = contentType, CannedACL = S3CannedACL.Private
            }, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed) { return false; }
    }

    public Task DeleteObjectAsync(string objectKey, CancellationToken ct) =>
        s3Client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucketName, Key = objectKey }, ct);

    public Task<string> GeneratePresignedDownloadUrlAsync(string objectKey, TimeSpan expiration, CancellationToken ct) =>
        s3Client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _bucketName, Key = objectKey, Verb = HttpVerb.GET, Expires = DateTime.UtcNow.Add(expiration)
        });
}
