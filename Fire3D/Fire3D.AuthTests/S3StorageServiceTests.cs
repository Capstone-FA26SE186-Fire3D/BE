using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Fire3D.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fire3D.AuthTests;

// The adapter is exercised through IAmazonS3 only; no AWS endpoint or credential is used.
public sealed class S3StorageServiceTests
{
    private const string Bucket = "storage-test-private";
    private static S3StorageService Service(IAmazonS3 client, ILogger<S3StorageService>? logger = null) => new(client,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AWS:BucketName"] = Bucket }).Build(),
        logger ?? new ListLogger());
    private static AmazonS3Exception S3Error(HttpStatusCode status) => new("fake provider failure") { StatusCode = status, ErrorCode = "Fake" };

    [Fact]
    public async Task Upload_puts_the_stream_with_length_and_type_without_closing_it()
    {
        await using var body = new MemoryStream([1, 2, 3]);
        PutObjectRequest? sent = null;
        var client = ResetProxy.For<IAmazonS3>((method, args) =>
        {
            Assert.Equal("PutObjectAsync", method);
            sent = Assert.IsType<PutObjectRequest>(args[0]);
            return Task.FromResult(new PutObjectResponse());
        });
        await Service(client).UploadObjectAsync("avatars/staging/key", body, 3, "image/png", default);
        Assert.Equal(Bucket, sent!.BucketName);
        Assert.Equal("avatars/staging/key", sent.Key);
        Assert.Same(body, sent.InputStream);
        Assert.False(sent.AutoCloseStream);
        Assert.Equal(3, sent.Headers.ContentLength);
        Assert.Equal("image/png", sent.Headers.ContentType);
        Assert.True(body.CanRead);
    }

    [Fact]
    public async Task Upload_logs_provider_failure_and_rethrows_the_original_exception()
    {
        var error = S3Error(HttpStatusCode.Forbidden);
        var logger = new ListLogger();
        var client = ResetProxy.For<IAmazonS3>((_, _) => Task.FromException<PutObjectResponse>(error));
        Assert.Same(error, await Assert.ThrowsAsync<AmazonS3Exception>(() =>
            Service(client, logger).UploadObjectAsync("avatars/staging/key", new MemoryStream(), 0, "image/png", default)));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("avatars/staging/key", entry.Message);
        Assert.Contains("Forbidden", entry.Message);
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public async Task Verify_object_exists_compares_the_stored_length(long expected, bool exists)
    {
        var client = ResetProxy.For<IAmazonS3>((method, args) =>
        {
            Assert.Equal("GetObjectMetadataAsync", method);
            var request = Assert.IsType<GetObjectMetadataRequest>(args[0]);
            Assert.Equal((Bucket, "object"), (request.BucketName, request.Key));
            return Task.FromResult(new GetObjectMetadataResponse { ContentLength = 10 });
        });
        Assert.Equal(exists, await Service(client).VerifyObjectExistsAsync("object", expected, default));
    }

    [Fact]
    public async Task Verify_object_exists_is_false_only_for_missing_objects()
    {
        var missing = ResetProxy.For<IAmazonS3>((_, _) => Task.FromException<GetObjectMetadataResponse>(S3Error(HttpStatusCode.NotFound)));
        Assert.False(await Service(missing).VerifyObjectExistsAsync("object", 1, default));
        var unavailable = ResetProxy.For<IAmazonS3>((_, _) => Task.FromException<GetObjectMetadataResponse>(S3Error(HttpStatusCode.ServiceUnavailable)));
        await Assert.ThrowsAsync<AmazonS3Exception>(() => Service(unavailable).VerifyObjectExistsAsync("object", 1, default));
    }

    [Fact]
    public async Task Metadata_maps_length_type_and_etag_and_returns_null_when_missing()
    {
        var client = ResetProxy.For<IAmazonS3>((_, args) =>
        {
            Assert.Equal("object", Assert.IsType<GetObjectMetadataRequest>(args[0]).Key);
            var response = new GetObjectMetadataResponse { ContentLength = 42, ETag = "\"etag\"" };
            response.Headers.ContentType = "image/webp";
            return Task.FromResult(response);
        });
        var metadata = await Service(client).GetObjectMetadataAsync("object", default);
        Assert.Equal(new(42, "image/webp", "\"etag\""), metadata);

        var missing = ResetProxy.For<IAmazonS3>((_, _) => Task.FromException<GetObjectMetadataResponse>(S3Error(HttpStatusCode.NotFound)));
        Assert.Null(await Service(missing).GetObjectMetadataAsync("object", default));
        var forbidden = ResetProxy.For<IAmazonS3>((_, _) => Task.FromException<GetObjectMetadataResponse>(S3Error(HttpStatusCode.Forbidden)));
        await Assert.ThrowsAsync<AmazonS3Exception>(() => Service(forbidden).GetObjectMetadataAsync("object", default));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    public async Task Reads_return_null_when_the_object_is_missing_or_changed(HttpStatusCode status)
    {
        var client = ResetProxy.For<IAmazonS3>((_, _) => Task.FromException<GetObjectResponse>(S3Error(status)));
        Assert.Null(await Service(client).ReadObjectPrefixAsync("object", 12, "etag", default));
        Assert.Null(await Service(client).ReadObjectAsync("object", 100, "etag", default));
    }

    [Fact]
    public async Task Reads_propagate_other_provider_failures()
    {
        var client = ResetProxy.For<IAmazonS3>((_, _) => Task.FromException<GetObjectResponse>(S3Error(HttpStatusCode.InternalServerError)));
        await Assert.ThrowsAsync<AmazonS3Exception>(() => Service(client).ReadObjectPrefixAsync("object", 12, "etag", default));
        await Assert.ThrowsAsync<AmazonS3Exception>(() => Service(client).ReadObjectAsync("object", 100, "etag", default));
    }

    [Fact]
    public async Task Conditional_copy_pins_the_source_etag_and_replaces_the_content_type()
    {
        CopyObjectRequest? sent = null;
        var client = ResetProxy.For<IAmazonS3>((method, args) =>
        {
            Assert.Equal("CopyObjectAsync", method);
            sent = Assert.IsType<CopyObjectRequest>(args[0]);
            return Task.FromResult(new CopyObjectResponse());
        });
        Assert.True(await Service(client).CopyObjectIfUnchangedAsync("staging", "source-etag", "final", "image/jpeg", default));
        Assert.Equal((Bucket, "staging", Bucket, "final"), (sent!.SourceBucket, sent.SourceKey, sent.DestinationBucket, sent.DestinationKey));
        Assert.Equal("source-etag", sent.ETagToMatch);
        Assert.Equal(S3MetadataDirective.REPLACE, sent.MetadataDirective);
        Assert.Equal("image/jpeg", sent.ContentType);
    }

    [Fact]
    public async Task Conditional_copy_is_false_when_the_source_changed_and_throws_for_other_failures()
    {
        var changed = ResetProxy.For<IAmazonS3>((_, _) => Task.FromException<CopyObjectResponse>(S3Error(HttpStatusCode.PreconditionFailed)));
        Assert.False(await Service(changed).CopyObjectIfUnchangedAsync("staging", "etag", "final", "image/png", default));
        var missing = ResetProxy.For<IAmazonS3>((_, _) => Task.FromException<CopyObjectResponse>(S3Error(HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<AmazonS3Exception>(() => Service(missing).CopyObjectIfUnchangedAsync("staging", "etag", "final", "image/png", default));
    }

    [Fact]
    public async Task Delete_targets_the_configured_bucket()
    {
        DeleteObjectRequest? sent = null;
        var client = ResetProxy.For<IAmazonS3>((method, args) =>
        {
            Assert.Equal("DeleteObjectAsync", method);
            sent = Assert.IsType<DeleteObjectRequest>(args[0]);
            return Task.FromResult(new DeleteObjectResponse());
        });
        await Service(client).DeleteObjectAsync("avatars/users/old", default);
        Assert.Equal((Bucket, "avatars/users/old"), (sent!.BucketName, sent.Key));
    }

    [Fact]
    public async Task Presigned_upload_url_is_a_put_for_the_declared_type()
    {
        GetPreSignedUrlRequest? sent = null;
        var client = ResetProxy.For<IAmazonS3>((method, args) =>
        {
            Assert.Equal("GetPreSignedURLAsync", method);
            sent = Assert.IsType<GetPreSignedUrlRequest>(args[0]);
            return Task.FromResult("https://storage.test/signed");
        });
        var before = DateTime.UtcNow;
        Assert.Equal("https://storage.test/signed", await Service(client).GeneratePresignedUploadUrlAsync("staging", "image/png", TimeSpan.FromMinutes(5), default));
        Assert.Equal((Bucket, "staging", HttpVerb.PUT, "image/png"), (sent!.BucketName, sent.Key, sent.Verb, sent.ContentType));
        Assert.InRange(sent.Expires!.Value, before.AddMinutes(5), DateTime.UtcNow.AddMinutes(5));
    }

    private sealed class ListLogger : ILogger<S3StorageService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
