namespace Fire3D.Application.Storage;

public interface IStorageService
{
    /// <summary>Uploads a server-received stream as a private object.</summary>
    Task UploadObjectAsync(string objectKey, Stream content, long contentLength, string contentType, CancellationToken ct);

    /// <summary>
    /// Generates a presigned URL for uploading a file directly to the storage provider.
    /// </summary>
    Task<string> GeneratePresignedUploadUrlAsync(string objectKey, string mimeType, TimeSpan expiration, CancellationToken ct);

    /// <summary>
    /// Verifies that an object exists in storage and matches the expected size.
    /// </summary>
    Task<bool> VerifyObjectExistsAsync(string objectKey, long expectedSizeBytes, CancellationToken ct);

    Task<StorageObjectMetadata?> GetObjectMetadataAsync(string objectKey, CancellationToken ct);
    Task<byte[]?> ReadObjectPrefixAsync(string objectKey, int length, string expectedETag, CancellationToken ct);
    /// <summary>Reads an inspected object version with a strict size limit. Null means it changed or disappeared.</summary>
    Task<byte[]?> ReadObjectAsync(string objectKey, long maxBytes, string expectedETag, CancellationToken ct);
    /// <summary>Copies only the object version that was inspected. False means the source changed.</summary>
    Task<bool> CopyObjectIfUnchangedAsync(string sourceKey, string sourceETag, string destinationKey, string contentType, CancellationToken ct);
    Task DeleteObjectAsync(string objectKey, CancellationToken ct);
    Task<string> GeneratePresignedDownloadUrlAsync(string objectKey, TimeSpan expiration, CancellationToken ct);
}

public sealed record StorageObjectMetadata(long ContentLength, string? ContentType, string? ETag);
