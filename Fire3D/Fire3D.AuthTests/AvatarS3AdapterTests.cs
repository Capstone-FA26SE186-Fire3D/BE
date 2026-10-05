using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Fire3D.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class AvatarS3AdapterTests
{
    private static S3StorageService Service(IAmazonS3 client) => new(client,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AWS:BucketName"] = "avatar-test-private" }).Build(),
        NullLogger<S3StorageService>.Instance);

    [Fact]
    public async Task Prefix_read_handles_fragmented_network_stream_and_pins_source_etag()
    {
        var bytes = Enumerable.Range(0, 12).Select(x => (byte)x).ToArray();
        var client = ResetProxy.For<IAmazonS3>((method, args) =>
        {
            var request = Assert.IsType<GetObjectRequest>(args[0]);
            Assert.Equal("source-etag", request.EtagToMatch);
            return Task.FromResult(new GetObjectResponse { ResponseStream = new FragmentedStream(bytes), ContentLength = bytes.Length });
        });
        Assert.Equal(bytes, await Service(client).ReadObjectPrefixAsync("avatars/staging/test", 12, "source-etag", default));
    }

    [Fact]
    public async Task Body_read_is_bounded_even_when_provider_content_length_is_incorrect()
    {
        var body = new FragmentedStream(new byte[1000]);
        var client = ResetProxy.For<IAmazonS3>((_, args) =>
        {
            Assert.Equal("source-etag", Assert.IsType<GetObjectRequest>(args[0]).EtagToMatch);
            return Task.FromResult(new GetObjectResponse { ResponseStream = body, ContentLength = 5 });
        });
        Assert.Null(await Service(client).ReadObjectAsync("avatars/staging/test", 16, "source-etag", default));
        Assert.InRange(body.BytesRead, 1, 17);
    }

    [Fact]
    public async Task Private_download_url_contains_s3_signature_and_needs_no_api_bearer()
    {
        // SDK signing is offline with fake credentials; no AWS request or real object is used.
        using var client = new AmazonS3Client(new BasicAWSCredentials("test-access", "test-secret"), RegionEndpoint.APSoutheast1);
        var url = new Uri(await Service(client).GeneratePresignedDownloadUrlAsync("avatars/users/test", TimeSpan.FromMinutes(5), default));
        Assert.Equal("https", url.Scheme);
        Assert.EndsWith("amazonaws.com", url.Host);
        Assert.Contains("X-Amz-Signature=", url.Query);
        Assert.Contains("X-Amz-Expires=300", url.Query);
        Assert.DoesNotContain("/api/me/avatar", url.AbsolutePath);
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = Read(buffer.Span[..Math.Min(2, buffer.Length)]);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            BytesRead += (int)(Length - Position);
            return base.CopyToAsync(destination, bufferSize, cancellationToken);
        }
    }
}
