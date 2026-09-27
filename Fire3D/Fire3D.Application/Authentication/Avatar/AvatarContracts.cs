using Fire3D.Domain.Entities;

namespace Fire3D.Application.Authentication.Avatar;

public sealed record AvatarUploadIntentRequest(string ContentType, long ContentLength);
public sealed record AvatarUploadIntentResponse(Guid UploadId, string UploadUrl, DateTime ExpiresAt);
public sealed record CompleteAvatarUploadRequest(Guid UploadId);
public sealed record AvatarResponse(string Url, DateTime ExpiresAt);
public sealed record AvatarFinalizeResult(bool Finalized, string? PreviousObjectKey);
public sealed record AvatarDeleteResult(bool Deleted, string? PreviousObjectKey);

public interface IAvatarStore
{
    Task SaveUploadIntentAsync(AvatarUploadIntent intent, CancellationToken ct);
    Task<AvatarUploadIntent?> FindUploadIntentAsync(Guid id, Guid userId, CancellationToken ct);
    Task<AvatarFinalizeResult> FinalizeUploadAsync(Guid intentId, Guid userId, string objectKey, DateTime completedAt, CancellationToken ct);
    Task<AvatarDeleteResult> DeleteAvatarAsync(Guid userId, DateTime deletedAt, CancellationToken ct);
}

public interface IAvatarService
{
    Task<AuthResult<AvatarUploadIntentResponse>> CreateUploadIntentAsync(Guid userId, AvatarUploadIntentRequest request, CancellationToken ct);
    Task<AuthResult<AvatarResponse>> CompleteUploadAsync(Guid userId, CompleteAvatarUploadRequest request, CancellationToken ct);
    Task<AuthResult<AvatarResponse>> GetAvatarAsync(Guid userId, CancellationToken ct);
    Task<AuthResult<object>> DeleteAvatarAsync(Guid userId, CancellationToken ct);
}
