namespace Fire3D.Application.Storage;

public interface IStorageService
{
    /// <summary>
    /// Generates a presigned URL for uploading a file directly to the storage provider.
    /// </summary>
    Task<string> GeneratePresignedUploadUrlAsync(string objectKey, string mimeType, TimeSpan expiration, CancellationToken ct);

    /// <summary>
    /// Verifies that an object exists in storage and matches the expected size.
    /// </summary>
    Task<bool> VerifyObjectExistsAsync(string objectKey, long expectedSizeBytes, CancellationToken ct);

    Task<StorageObjectMetadata?> GetObjectMetadataAsync(string objectKey, CancellationToken ct);
    Task<byte[]?> ReadObjectPrefixAsync(string objectKey, int length, CancellationToken ct);
    Task CopyObjectAsync(string sourceKey, string destinationKey, string contentType, CancellationToken ct);
    Task DeleteObjectAsync(string objectKey, CancellationToken ct);
    Task<string> GeneratePresignedDownloadUrlAsync(string objectKey, TimeSpan expiration, CancellationToken ct);
}

public sealed record StorageObjectMetadata(long ContentLength, string? ContentType);
