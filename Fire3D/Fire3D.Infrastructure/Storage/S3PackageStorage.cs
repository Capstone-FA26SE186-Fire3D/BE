using Amazon.S3;
using Amazon.S3.Model;
using Fire3D.Application.Ifc;
using Microsoft.Extensions.Configuration;

namespace Fire3D.Infrastructure.Storage;

/// <summary>
/// BE#110: S3 access for the Unity package worker and package downloads. The worker gets only short URLs for its own inputs and
/// output prefix; uploads are signed with x-amz-checksum-sha256 so S3 rejects a body whose SHA-256 differs (BadDigest).
/// </summary>
public sealed class S3PackageStorage(IAmazonS3 client, IConfiguration configuration) : IPackageStorage
{
    private readonly string bucket = S3StorageService.ResolveBucketName(configuration);

    public async Task<SignedDownload?> SignVerifiedGetAsync(string key, long sizeBytes, TimeSpan lifetime, CancellationToken ct)
    {
        var head = await HeadAsync(key, ct);
        if (head is null || sizeBytes <= 0 || head.Value.Size != sizeBytes) return null;
        return await SignGetAsync(key, lifetime, ct);
    }

    public async Task<SignedDownload> SignGetAsync(string key, TimeSpan lifetime, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var expires = DateTimeOffset.UtcNow.Add(lifetime);
        var url = await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest { BucketName = bucket, Key = key, Verb = HttpVerb.GET, Expires = expires.UtcDateTime });
        return new(url, expires);
    }

    public async Task<SignedUpload> SignPutAsync(string key, string sha256Hex, TimeSpan lifetime, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var expires = DateTimeOffset.UtcNow.Add(lifetime);
        var checksum = Convert.ToBase64String(Convert.FromHexString(sha256Hex));
        var request = new GetPreSignedUrlRequest { BucketName = bucket, Key = key, Verb = HttpVerb.PUT, Expires = expires.UtcDateTime };
        // Signed headers: the upload must send exactly these, and S3 checks the body against the checksum.
        request.Headers["x-amz-checksum-sha256"] = checksum;
        request.Headers["x-amz-sdk-checksum-algorithm"] = "SHA256";
        var url = await client.GetPreSignedURLAsync(request);
        return new(url, new Dictionary<string, string> { ["x-amz-checksum-sha256"] = checksum, ["x-amz-sdk-checksum-algorithm"] = "SHA256" }, expires);
    }

    public async Task<(long Size, string? Sha256Hex)?> HeadAsync(string key, CancellationToken ct)
    {
        try
        {
            var metadata = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key, ChecksumMode = ChecksumMode.ENABLED }, ct);
            // A multipart upload stores a composite checksum ("<base64>-<parts>"), which is not the file's SHA-256.
            var sha = metadata.ChecksumSHA256 is { Length: > 0 } stored && !stored.Contains('-')
                ? Convert.ToHexString(Convert.FromBase64String(stored)).ToLowerInvariant() : null;
            return (metadata.ContentLength, sha);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<byte[]?> ReadAsync(string key, long maxBytes, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, ct);
            if (response.ContentLength < 0 || response.ContentLength > maxBytes) return null;
            await using var buffer = new MemoryStream((int)response.ContentLength);
            await response.ResponseStream.CopyToAsync(buffer, ct);
            return buffer.Length > maxBytes ? null : buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }
}
