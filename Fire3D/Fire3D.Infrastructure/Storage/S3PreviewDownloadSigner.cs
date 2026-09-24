using Amazon.S3;
using Amazon.S3.Model;
using Fire3D.Application.Ifc;
using Microsoft.Extensions.Configuration;

namespace Fire3D.Infrastructure.Storage;

public sealed class S3PreviewDownloadSigner(IAmazonS3 client, IConfiguration configuration) : IPreviewDownloadSigner
{
    public async Task<SignedDownload> SignAsync(string storageKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        var url = await client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = S3StorageService.ResolveBucketName(configuration),
            Key = storageKey, Verb = HttpVerb.GET, Expires = expires.UtcDateTime
        });
        return new(url, expires);
    }
}
