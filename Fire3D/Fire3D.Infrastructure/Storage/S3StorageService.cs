using Amazon.S3;
using Fire3D.Application.Storage;
using Microsoft.Extensions.Configuration;

namespace Fire3D.Infrastructure.Storage;

public class S3StorageService(IAmazonS3 s3Client, IConfiguration configuration) : IStorageService
{
    private readonly string _bucketName = configuration["AWS:BucketName"] ?? Environment.GetEnvironmentVariable("S3_BUCKET_NAME") ?? "fire3d-uploads";

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
        catch
        {
            return false;
        }
    }
}
