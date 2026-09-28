using Fire3D.Application.Storage;

namespace Fire3D.Infrastructure.Storage;

public class StubStorageService : IStorageService
{
    public Task UploadObjectAsync(string objectKey, Stream content, long contentLength, string contentType, CancellationToken ct) => Task.CompletedTask;

    public Task<string> GeneratePresignedUploadUrlAsync(string objectKey, string mimeType, TimeSpan expiration, CancellationToken ct)
    {
        // Stub implementation: returns a fake presigned URL
        return Task.FromResult($"https://stub-storage.fire3d.local/upload/{objectKey}?Expires={DateTimeOffset.UtcNow.Add(expiration).ToUnixTimeSeconds()}&Signature=stub");
    }

    public Task<bool> VerifyObjectExistsAsync(string objectKey, long expectedSizeBytes, CancellationToken ct)
    {
        // Stub implementation: assume the object was successfully uploaded by the client
        return Task.FromResult(true);
    }

    public Task<StorageObjectMetadata?> GetObjectMetadataAsync(string objectKey, CancellationToken ct) =>
        Task.FromResult<StorageObjectMetadata?>(new(1, "image/png", "stub-etag"));
    public Task<byte[]?> ReadObjectPrefixAsync(string objectKey, int length, string expectedETag, CancellationToken ct) =>
        Task.FromResult<byte[]?>([137, 80, 78, 71, 13, 10, 26, 10]);
    public Task<byte[]?> ReadObjectAsync(string objectKey, long maxBytes, string expectedETag, CancellationToken ct) =>
        Task.FromResult<byte[]?>(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR42mNk+M/wHwAF/gL+X6bYJwAAAABJRU5ErkJggg=="));
    public Task<bool> CopyObjectIfUnchangedAsync(string sourceKey, string sourceETag, string destinationKey, string contentType, CancellationToken ct) => Task.FromResult(true);
    public Task DeleteObjectAsync(string objectKey, CancellationToken ct) => Task.CompletedTask;
    public Task<string> GeneratePresignedDownloadUrlAsync(string objectKey, TimeSpan expiration, CancellationToken ct) =>
        Task.FromResult($"https://stub-storage.fire3d.local/download/{objectKey}?Expires={DateTimeOffset.UtcNow.Add(expiration).ToUnixTimeSeconds()}&Signature=stub");
}
