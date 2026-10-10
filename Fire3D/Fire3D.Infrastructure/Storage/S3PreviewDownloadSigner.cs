using Amazon.S3;
using Amazon.S3.Model;
using Fire3D.Application.Ifc;
using Microsoft.Extensions.Configuration;

namespace Fire3D.Infrastructure.Storage;

public sealed class S3PreviewDownloadSigner(IAmazonS3 client, IConfiguration configuration) : IPreviewDownloadSigner
{
    public async Task<SignedDownload?> SignAsync(string storageKey, long expectedSizeBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bucket = S3StorageService.ResolveBucketName(configuration);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var metadata = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = storageKey }, timeout.Token);
            ct.ThrowIfCancellationRequested();
            if (expectedSizeBytes <= 0 || metadata.ContentLength != expectedSizeBytes) return null;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new PreviewStorageUnavailableException(ex); }
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        try
        {
            var url = await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
            {
                BucketName = bucket, Key = storageKey, Verb = HttpVerb.GET, Expires = expires.UtcDateTime
            });
            ct.ThrowIfCancellationRequested();
            return new(url, expires);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new PreviewStorageUnavailableException(ex); }
    }
}
