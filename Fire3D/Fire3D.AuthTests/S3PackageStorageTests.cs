using System.Net;
using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Fire3D.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fire3D.AuthTests;

/// <summary>S3PackageStorage against a fake IAmazonS3 (no network) and offline SDK signing with fixture credentials.</summary>
public sealed class S3PackageStorageTests
{
    private static readonly byte[] Bytes = "floor-bundle"u8.ToArray();
    private static readonly string Sha = Convert.ToHexStringLower(SHA256.HashData(Bytes));
    private static S3PackageStorage Storage(IAmazonS3 client) => new(client,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AWS:BucketName"] = "fet3d-package-test" }).Build());
    private static AmazonS3Exception Missing() => new("Not Found") { StatusCode = HttpStatusCode.NotFound };

    // HEAD answers for one stored object; everything else is missing.
    private static IAmazonS3 Client(string key, string? checksum, List<GetPreSignedUrlRequest>? signed = null) => ResetProxy.For<IAmazonS3>((method, args) => method switch
    {
        nameof(IAmazonS3.GetObjectMetadataAsync) when args[0] is GetObjectMetadataRequest { BucketName: "fet3d-package-test", ChecksumMode: var mode } head
            && mode == ChecksumMode.ENABLED => head.Key == key
                ? Task.FromResult(new GetObjectMetadataResponse { ContentLength = Bytes.Length, ChecksumSHA256 = checksum })
                : Task.FromException<GetObjectMetadataResponse>(Missing()),
        nameof(IAmazonS3.GetPreSignedURLAsync) => Sign((GetPreSignedUrlRequest)args[0]!, signed),
        _ => throw new Exception("Unexpected S3 call: " + method)
    });
    private static Task<string> Sign(GetPreSignedUrlRequest request, List<GetPreSignedUrlRequest>? signed)
    {
        signed?.Add(request);
        return Task.FromResult($"https://fet3d-package-test.s3.test/{request.Key}?signed");
    }

    [Fact]
    public async Task Verified_get_signs_only_an_existing_object_with_the_expected_size()
    {
        var signed = new List<GetPreSignedUrlRequest>();
        var storage = Storage(Client("worker-output/a/floor_L1", Convert.ToBase64String(SHA256.HashData(Bytes)), signed));
        var download = await storage.SignVerifiedGetAsync("worker-output/a/floor_L1", Bytes.Length, TimeSpan.FromMinutes(15), default);
        Assert.Equal("https://fet3d-package-test.s3.test/worker-output/a/floor_L1?signed", download!.Url);
        Assert.InRange(download.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
        var request = Assert.Single(signed);
        Assert.Equal((HttpVerb.GET, "fet3d-package-test"), (request.Verb, request.BucketName));

        Assert.Null(await storage.SignVerifiedGetAsync("worker-output/a/floor_L1", Bytes.Length + 1, TimeSpan.FromMinutes(15), default));
        Assert.Null(await storage.SignVerifiedGetAsync("worker-output/a/floor_L1", 0, TimeSpan.FromMinutes(15), default));
        Assert.Null(await storage.SignVerifiedGetAsync("worker-output/a/missing", Bytes.Length, TimeSpan.FromMinutes(15), default));
        Assert.Single(signed);
    }

    [Fact]
    public async Task Head_reports_the_sha256_only_for_a_single_part_checksum()
    {
        var whole = await Storage(Client("k", Convert.ToBase64String(SHA256.HashData(Bytes)))).HeadAsync("k", default);
        Assert.Equal((Bytes.Length, Sha), whole!.Value);
        // A multipart upload's composite checksum is not the file's SHA-256.
        var multipart = await Storage(Client("k", Convert.ToBase64String(SHA256.HashData(Bytes)) + "-3")).HeadAsync("k", default);
        Assert.Equal((Bytes.Length, (string?)null), multipart!.Value);
        Assert.Null((await Storage(Client("k", null)).HeadAsync("k", default))!.Value.Sha256Hex);
        Assert.Null(await Storage(Client("k", null)).HeadAsync("other", default));
    }

    [Fact]
    public async Task Head_does_not_hide_provider_errors_other_than_not_found()
    {
        var denied = ResetProxy.For<IAmazonS3>((_, _) => Task.FromException<GetObjectMetadataResponse>(new AmazonS3Exception("Access Denied") { StatusCode = HttpStatusCode.Forbidden }));
        await Assert.ThrowsAsync<AmazonS3Exception>(() => Storage(denied).HeadAsync("k", default));
    }

    [Fact]
    public async Task Read_returns_the_whole_object_within_the_limit_and_null_when_missing_or_too_large()
    {
        var client = ResetProxy.For<IAmazonS3>((method, args) =>
        {
            Assert.Equal(nameof(IAmazonS3.GetObjectAsync), method);
            var request = (GetObjectRequest)args[0]!;
            Assert.Equal("fet3d-package-test", request.BucketName);
            return request.Key == "manifest.json"
                ? Task.FromResult(new GetObjectResponse { ContentLength = Bytes.Length, ResponseStream = new MemoryStream(Bytes) })
                : Task.FromException<GetObjectResponse>(Missing());
        });
        Assert.Equal(Bytes, await Storage(client).ReadAsync("manifest.json", Bytes.Length, default));
        Assert.Null(await Storage(client).ReadAsync("manifest.json", Bytes.Length - 1, default));
        Assert.Null(await Storage(client).ReadAsync("missing.json", 1024, default));
        // A provider that understates Content-Length cannot push more than maxBytes through.
        var understated = ResetProxy.For<IAmazonS3>((_, _) => Task.FromResult(new GetObjectResponse { ContentLength = 3, ResponseStream = new MemoryStream(Bytes) }));
        Assert.Null(await Storage(understated).ReadAsync("manifest.json", 5, default));
    }

    [Fact]
    public async Task Get_url_is_signed_offline_for_the_package_bucket()
    {
        // SDK signing is offline with fixture credentials; no AWS request is made.
        using var client = new AmazonS3Client(new BasicAWSCredentials("AKIDEXAMPLE", "fixture-secret"), RegionEndpoint.APSoutheast1);
        var download = await Storage(client).SignGetAsync("worker-output/o/j/a/manifest.json", TimeSpan.FromMinutes(10), default);
        var url = new Uri(download.Url);
        Assert.Contains("fet3d-package-test", url.Host + url.AbsolutePath);
        Assert.EndsWith("/worker-output/o/j/a/manifest.json", url.AbsolutePath);
        Assert.Contains("X-Amz-Signature=", url.Query);
        Assert.Contains("X-Amz-Expires=", url.Query);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Storage(client).SignGetAsync("k", TimeSpan.FromMinutes(10), cancelled.Token));
    }
}
