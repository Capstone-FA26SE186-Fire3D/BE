using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Fire3D.Application.Ifc;
using Microsoft.Extensions.Configuration;

namespace Fire3D.Infrastructure.Storage;

public sealed class IfcSourceInspector(IAmazonS3 s3, IConfiguration configuration) : IIfcSourceInspector
{
    private readonly string bucket = S3StorageService.ResolveBucketName(configuration);
    public async Task<Fire3D.Application.Storage.StorageObjectMetadata?> MetadataAsync(string key, CancellationToken ct)
    {
        try
        {
            var metadata = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key }, ct);
            return new(metadata.ContentLength, metadata.Headers.ContentType, metadata.ETag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
        catch (AmazonS3Exception ex) { throw new IfcStorageFailure(ex); }
    }
    public async Task<bool> CopyAsync(string key, string etag, string finalKey, CancellationToken ct)
    {
        try
        {
            await s3.CopyObjectAsync(new CopyObjectRequest { SourceBucket = bucket, SourceKey = key, ETagToMatch = etag,
                DestinationBucket = bucket, DestinationKey = finalKey, MetadataDirective = S3MetadataDirective.REPLACE, ContentType = "application/octet-stream" }, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed) { return false; }
        catch (AmazonS3Exception ex) { throw new IfcStorageFailure(ex); }
    }
    public async Task<IfcObjectDigest?> InspectAsync(string key, string etag, long maxBytes, CancellationToken ct)
    {
        try
        {
            using var response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key, EtagToMatch = etag }, ct);
            if (response.ContentLength > maxBytes || response.ETag != etag) return null;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024]; long length = 0;
            while (true)
            {
                var remaining = maxBytes - length;
                var read = await response.ResponseStream.ReadAsync(buffer.AsMemory(0, remaining >= buffer.Length ? buffer.Length : (int)remaining + 1), ct);
                if (read == 0) break;
                length += read; if (length > maxBytes) return null;
                hash.AppendData(buffer, 0, read);
            }
            return new(length, Convert.ToHexStringLower(hash.GetHashAndReset()), etag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed) { return null; }
        catch (AmazonS3Exception ex) { throw new IfcStorageFailure(ex); }
    }
}
