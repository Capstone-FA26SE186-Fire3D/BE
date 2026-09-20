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
}
