using Fire3D.Domain.Entities;

namespace Fire3D.Application.Authentication.Avatar;

public sealed record AvatarUploadIntentRequest(string ContentType, long ContentLength);
public sealed record AvatarUploadIntentResponse(Guid UploadId, string UploadUrl, DateTime ExpiresAt);
public sealed record CompleteAvatarUploadRequest(Guid UploadId);
public sealed record AvatarResponse(string Url, DateTime ExpiresAt, long ProfileRevision);
public sealed record AvatarFinalizeResult(AvatarFinalizeStatus Status, string? PreviousObjectKey)
{
    public bool Finalized => Status == AvatarFinalizeStatus.Finalized;
}
public sealed record AvatarDeleteResult(AvatarDeleteStatus Status, string? PreviousObjectKey)
{
    public bool Deleted => Status == AvatarDeleteStatus.Deleted;
}
public enum AvatarFinalizeStatus { Finalized, PreconditionFailed, Unavailable }
public enum AvatarDeleteStatus { Deleted, PreconditionFailed, Unavailable }
public sealed record AvatarCleanupJob(Guid Id, string ObjectKey, Guid LeaseToken, int Attempt);

public interface IAvatarStore
{
    Task SaveUploadIntentAsync(AvatarUploadIntent intent, CancellationToken ct);
    Task<AvatarUploadIntent?> FindUploadIntentAsync(Guid id, Guid userId, CancellationToken ct);
    Task<AvatarFinalizeResult> FinalizeUploadAsync(Guid intentId, Guid userId, long expectedProfileRevision, string objectKey, DateTime completedAt, CancellationToken ct);
    Task<AvatarDeleteResult> DeleteAvatarAsync(Guid userId, long expectedProfileRevision, DateTime deletedAt, CancellationToken ct);
}

public interface IAvatarCleanupStore
{
    Task QueueAsync(string objectKey, CancellationToken ct);
    Task<AvatarCleanupJob?> ClaimAsync(CancellationToken ct);
    Task<bool> IsReferencedAsync(string objectKey, CancellationToken ct);
    Task CompleteAsync(AvatarCleanupJob job, CancellationToken ct);
    Task FailAsync(AvatarCleanupJob job, CancellationToken ct);
}

public interface IAvatarService
{
    Task<AuthResult<AvatarUploadIntentResponse>> CreateUploadIntentAsync(Guid userId, AvatarUploadIntentRequest request, CancellationToken ct);
    Task<AuthResult<AvatarResponse>> UploadFileAsync(Guid userId, long expectedProfileRevision, string contentType, long contentLength, Stream content, CancellationToken ct);
    Task<AuthResult<AvatarResponse>> CompleteUploadAsync(Guid userId, long expectedProfileRevision, CompleteAvatarUploadRequest request, CancellationToken ct);
    Task<AuthResult<AvatarResponse>> GetAvatarAsync(Guid userId, CancellationToken ct);
    Task<AuthResult<object>> DeleteAvatarAsync(Guid userId, long expectedProfileRevision, CancellationToken ct);
}
