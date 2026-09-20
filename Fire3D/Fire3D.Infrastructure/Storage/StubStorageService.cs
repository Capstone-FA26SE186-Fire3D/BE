using Fire3D.Application.Storage;

namespace Fire3D.Infrastructure.Storage;

public class StubStorageService : IStorageService
{
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
}
