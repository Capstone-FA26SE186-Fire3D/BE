using Fire3D.Application.Storage;
using Fire3D.Domain.Entities;

namespace Fire3D.Application.Authentication.Avatar;

public sealed class AvatarService(IAuthStore authStore, IAvatarStore avatarStore, IStorageService storage, TimeProvider clock) : IAvatarService
{
    private static readonly TimeSpan UrlLifetime = TimeSpan.FromMinutes(5);

    public async Task<AuthResult<AvatarUploadIntentResponse>> CreateUploadIntentAsync(Guid userId, AvatarUploadIntentRequest request, CancellationToken ct)
    {
        if (request is null) return AuthResult<AvatarUploadIntentResponse>.Fail("VALIDATION_ERROR", "Upload request is required.", 400);
        var invalid = AvatarUploadRules.ValidateIntent(request.ContentType, request.ContentLength);
        if (invalid is not null) return AuthResult<AvatarUploadIntentResponse>.Fail(invalid.Code, invalid.Message, 400);
        if (!await IsAvailableAsync(userId, ct)) return AuthResult<AvatarUploadIntentResponse>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);
        var now = UtcNow();
        var intent = new AvatarUploadIntent
        {
            Id = Guid.NewGuid(), UserId = userId,
            StagingObjectKey = $"avatars/staging/{userId:N}/{Guid.NewGuid():N}",
            ContentType = request.ContentType.Trim().ToLowerInvariant(), ExpectedSizeBytes = request.ContentLength,
            CreatedAt = now, ExpiresAt = now.Add(UrlLifetime)
        };
        await avatarStore.SaveUploadIntentAsync(intent, ct);
        var url = await storage.GeneratePresignedUploadUrlAsync(intent.StagingObjectKey, intent.ContentType, UrlLifetime, ct);
        return AuthResult<AvatarUploadIntentResponse>.Ok(new(intent.Id, url, intent.ExpiresAt));
    }

    public async Task<AuthResult<AvatarResponse>> CompleteUploadAsync(Guid userId, CompleteAvatarUploadRequest request, CancellationToken ct)
    {
        if (request is null || request.UploadId == Guid.Empty) return AuthResult<AvatarResponse>.Fail("VALIDATION_ERROR", "UploadId is required.", 400);
        if (!await IsAvailableAsync(userId, ct)) return AuthResult<AvatarResponse>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);
        var intent = await avatarStore.FindUploadIntentAsync(request.UploadId, userId, ct);
        var now = UtcNow();
        if (intent is null || intent.CompletedAt.HasValue || intent.ExpiresAt <= now)
            return AuthResult<AvatarResponse>.Fail("INVALID_AVATAR_UPLOAD", "Avatar upload is unavailable or expired.", 409);
        var metadata = await storage.GetObjectMetadataAsync(intent.StagingObjectKey, ct);
        if (metadata is null || metadata.ContentLength != intent.ExpectedSizeBytes || !string.Equals(metadata.ContentType, intent.ContentType, StringComparison.OrdinalIgnoreCase))
            return AuthResult<AvatarResponse>.Fail("INVALID_AVATAR_UPLOAD", "Avatar upload metadata does not match the intent.", 400);
        var prefix = await storage.ReadObjectPrefixAsync(intent.StagingObjectKey, 12, ct);
        var invalid = AvatarUploadRules.ValidateImageSignature(intent.ContentType, prefix ?? []);
        if (invalid is not null) return AuthResult<AvatarResponse>.Fail(invalid.Code, invalid.Message, 400);
        var finalKey = $"avatars/users/{userId:N}/{intent.Id:N}";
        await storage.CopyObjectAsync(intent.StagingObjectKey, finalKey, intent.ContentType, ct);
        var finalized = await avatarStore.FinalizeUploadAsync(intent.Id, userId, finalKey, now, ct);
        if (!finalized.Finalized)
        {
            await TryDeleteAsync(finalKey, ct);
            return AuthResult<AvatarResponse>.Fail("INVALID_AVATAR_UPLOAD", "Avatar upload is no longer available.", 409);
        }
        await TryDeleteAsync(intent.StagingObjectKey, ct);
        if (!string.IsNullOrWhiteSpace(finalized.PreviousObjectKey)) await TryDeleteAsync(finalized.PreviousObjectKey, ct);
        var url = await storage.GeneratePresignedDownloadUrlAsync(finalKey, UrlLifetime, ct);
        return AuthResult<AvatarResponse>.Ok(new(url, now.Add(UrlLifetime)));
    }

    public async Task<AuthResult<AvatarResponse>> GetAvatarAsync(Guid userId, CancellationToken ct)
    {
        var user = await AvailableUserAsync(userId, ct);
        if (user is null) return AuthResult<AvatarResponse>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);
        if (string.IsNullOrWhiteSpace(user.AvatarStorageKey)) return AuthResult<AvatarResponse>.Fail("AVATAR_NOT_FOUND", "Avatar is not set.", 404);
        var now = UtcNow();
        var url = await storage.GeneratePresignedDownloadUrlAsync(user.AvatarStorageKey, UrlLifetime, ct);
        return AuthResult<AvatarResponse>.Ok(new(url, now.Add(UrlLifetime)));
    }

    public async Task<AuthResult<object>> DeleteAvatarAsync(Guid userId, CancellationToken ct)
    {
        if (!await IsAvailableAsync(userId, ct)) return AuthResult<object>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);
        var result = await avatarStore.DeleteAvatarAsync(userId, UtcNow(), ct);
        if (result.Deleted && !string.IsNullOrWhiteSpace(result.PreviousObjectKey)) await TryDeleteAsync(result.PreviousObjectKey, ct);
        return AuthResult<object>.Ok(new { });
    }

    private async Task<bool> IsAvailableAsync(Guid userId, CancellationToken ct) => await AvailableUserAsync(userId, ct) is not null;
    private async Task<User?> AvailableUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await authStore.FindUserAsync(userId, ct);
        if (user is null || !user.IsActive || user.DeletedAt.HasValue) return null;
        return user.OrganizationId is null || await authStore.OrganizationIsActiveAsync(user.OrganizationId.Value, ct) ? user : null;
    }
    private DateTime UtcNow() => clock.GetUtcNow().UtcDateTime;
    private async Task TryDeleteAsync(string objectKey, CancellationToken ct)
    {
        try { await storage.DeleteObjectAsync(objectKey, ct); }
        catch { /* Database state is already committed; a later cleanup worker must reconcile this orphan. */ }
    }
}
