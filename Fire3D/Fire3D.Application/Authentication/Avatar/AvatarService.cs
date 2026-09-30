using Fire3D.Application.Storage;
using Fire3D.Domain.Entities;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;

namespace Fire3D.Application.Authentication.Avatar;

public sealed class AvatarService(IAuthStore authStore, IAvatarStore avatarStore, IAvatarCleanupStore cleanup, IStorageService storage, TimeProvider clock) : IAvatarService
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

    public async Task<AuthResult<AvatarResponse>> UploadFileAsync(Guid userId, long expectedProfileRevision, string contentType, long contentLength, Stream content, CancellationToken ct)
    {
        if (expectedProfileRevision < 1)
            return AuthResult<AvatarResponse>.Fail("VALIDATION_ERROR", "A valid profile ETag is required.", 400);
        if (content is null)
            return AuthResult<AvatarResponse>.Fail("VALIDATION_ERROR", "Avatar file is required.", 400);

        var intentResult = await CreateUploadIntentAsync(userId, new(contentType, contentLength), ct);
        if (!intentResult.IsSuccess) return AuthResult<AvatarResponse>.Fail(intentResult.Error!.Code, intentResult.Error.Message, intentResult.Error.Status);

        var intent = await avatarStore.FindUploadIntentAsync(intentResult.Value!.UploadId, userId, ct);
        if (intent is null)
            return AuthResult<AvatarResponse>.Fail("AVATAR_UPLOAD_UNAVAILABLE", "Avatar upload is unavailable.", 503);

        try
        {
            await storage.UploadObjectAsync(intent.StagingObjectKey, content, contentLength, intent.ContentType, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await QueueCleanupAsync(intent.StagingObjectKey, ct);
            return AuthResult<AvatarResponse>.Fail("AVATAR_UPLOAD_UNAVAILABLE", "Avatar upload failed. Try again.", 503);
        }

        var completed = await CompleteUploadAsync(userId, expectedProfileRevision, new(intent.Id), ct);
        if (!completed.IsSuccess) await QueueCleanupAsync(intent.StagingObjectKey, ct);
        return completed;
    }

    public async Task<AuthResult<AvatarResponse>> CompleteUploadAsync(Guid userId, long expectedProfileRevision, CompleteAvatarUploadRequest request, CancellationToken ct)
    {
        if (request is null || request.UploadId == Guid.Empty || expectedProfileRevision < 1) return AuthResult<AvatarResponse>.Fail("VALIDATION_ERROR", "UploadId and a valid profile ETag are required.", 400);
        if (!await IsAvailableAsync(userId, ct)) return AuthResult<AvatarResponse>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);
        var intent = await avatarStore.FindUploadIntentAsync(request.UploadId, userId, ct);
        var now = UtcNow();
        if (intent is null)
            return AuthResult<AvatarResponse>.Fail("INVALID_AVATAR_UPLOAD", "Avatar upload is unavailable or expired.", 409);
        if (intent.CompletedAt.HasValue)
        {
            if (intent.CompletedAt.Value < now.AddHours(-24))
                return AuthResult<AvatarResponse>.Fail("INVALID_AVATAR_UPLOAD", "Avatar upload replay window has expired.", 409);
            return await ReplayCompletedUploadAsync(userId, expectedProfileRevision, intent, ct);
        }
        if (intent.ExpiresAt <= now)
            return AuthResult<AvatarResponse>.Fail("INVALID_AVATAR_UPLOAD", "Avatar upload is unavailable or expired.", 409);
        var metadata = await storage.GetObjectMetadataAsync(intent.StagingObjectKey, ct);
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.ETag) || metadata.ContentLength != intent.ExpectedSizeBytes || !string.Equals(metadata.ContentType, intent.ContentType, StringComparison.OrdinalIgnoreCase))
            return AuthResult<AvatarResponse>.Fail("INVALID_AVATAR_UPLOAD", "Avatar upload metadata does not match the intent.", 400);
        var prefix = await storage.ReadObjectPrefixAsync(intent.StagingObjectKey, 12, metadata.ETag, ct);
        var invalid = AvatarUploadRules.ValidateImageSignature(intent.ContentType, prefix ?? []);
        if (invalid is not null) return AuthResult<AvatarResponse>.Fail(invalid.Code, invalid.Message, 400);
        var imageBytes = await storage.ReadObjectAsync(intent.StagingObjectKey, AvatarUploadRules.MaxBytes, metadata.ETag, ct);
        if (!IsSafeImage(imageBytes, intent.ContentType))
            return AuthResult<AvatarResponse>.Fail("INVALID_AVATAR_CONTENT", "Avatar must be a valid, single-frame image no larger than 4096 by 4096 pixels.", 400);
        // A complete attempt must never share a destination key. A losing concurrent request can
        // then remove only its own orphan without deleting the winner's stored avatar.
        var finalKey = $"avatars/users/{userId:N}/{Guid.NewGuid():N}";
        if (!await storage.CopyObjectIfUnchangedAsync(intent.StagingObjectKey, metadata.ETag, finalKey, intent.ContentType, ct))
            return AuthResult<AvatarResponse>.Fail("AVATAR_UPLOAD_CHANGED", "Avatar upload changed before it could be completed. Upload again.", 409);
        var completedAt = UtcNow();
        var finalized = await avatarStore.FinalizeUploadAsync(intent.Id, userId, expectedProfileRevision, finalKey, completedAt, ct);
        if (!finalized.Finalized)
        {
            await QueueCleanupAsync(finalKey, ct);
            return finalized.Status == AvatarFinalizeStatus.PreconditionFailed
                ? AuthResult<AvatarResponse>.Fail("PRECONDITION_FAILED", "The profile changed. Reload it and retry.", 412)
                : AuthResult<AvatarResponse>.Fail("INVALID_AVATAR_UPLOAD", "Avatar upload is no longer available.", 409);
        }
        await QueueCleanupAsync(intent.StagingObjectKey, ct);
        var url = await storage.GeneratePresignedDownloadUrlAsync(finalKey, UrlLifetime, ct);
        return AuthResult<AvatarResponse>.Ok(new(url, completedAt.Add(UrlLifetime), expectedProfileRevision + 1));
    }

    public async Task<AuthResult<AvatarResponse>> GetAvatarAsync(Guid userId, CancellationToken ct)
    {
        var user = await AvailableUserAsync(userId, ct);
        if (user is null) return AuthResult<AvatarResponse>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);
        if (string.IsNullOrWhiteSpace(user.AvatarStorageKey)) return AuthResult<AvatarResponse>.Fail("AVATAR_NOT_FOUND", "Avatar is not set.", 404);
        var now = UtcNow();
        var url = await storage.GeneratePresignedDownloadUrlAsync(user.AvatarStorageKey, UrlLifetime, ct);
        return AuthResult<AvatarResponse>.Ok(new(url, now.Add(UrlLifetime), user.ProfileRevision));
    }

    public async Task<AuthResult<object>> DeleteAvatarAsync(Guid userId, long expectedProfileRevision, CancellationToken ct)
    {
        if (expectedProfileRevision < 1) return AuthResult<object>.Fail("VALIDATION_ERROR", "A valid profile ETag is required.", 400);
        if (!await IsAvailableAsync(userId, ct)) return AuthResult<object>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);
        var result = await avatarStore.DeleteAvatarAsync(userId, expectedProfileRevision, UtcNow(), ct);
        if (result.Status == AvatarDeleteStatus.PreconditionFailed)
            return AuthResult<object>.Fail("PRECONDITION_FAILED", "The profile changed. Reload it and retry.", 412);
        if (result.Status == AvatarDeleteStatus.Unavailable)
            return AuthResult<object>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);
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

    private async Task<AuthResult<AvatarResponse>> ReplayCompletedUploadAsync(Guid userId, long expectedProfileRevision,
        AvatarUploadIntent intent, CancellationToken ct)
    {
        var user = await AvailableUserAsync(userId, ct);
        if (user is null) return AuthResult<AvatarResponse>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);
        if (string.IsNullOrWhiteSpace(intent.FinalObjectKey) || user.AvatarStorageKey != intent.FinalObjectKey)
            return AuthResult<AvatarResponse>.Fail("AVATAR_UPLOAD_SUPERSEDED", "This upload was replaced by a newer profile change.", 409);
        var now = UtcNow();
        var url = await storage.GeneratePresignedDownloadUrlAsync(intent.FinalObjectKey, UrlLifetime, ct);
        return AuthResult<AvatarResponse>.Ok(new(url, now.Add(UrlLifetime), user.ProfileRevision));
    }

    private static bool IsSafeImage(byte[]? bytes, string contentType)
    {
        if (bytes is null) return false;
        try
        {
            var format = Image.DetectFormat(bytes);
            var info = Image.Identify(bytes);
            // Static formats do not always expose a frame metadata item; more than one does
            // identify an animation and is rejected before pixels are decoded.
            if (info.Width is <= 0 or > 4096 || info.Height is <= 0 or > 4096 || info.FrameMetadataCollection.Count > 1)
                return false;
            using var image = Image.Load(new DecoderOptions { MaxFrames = 1 }, bytes);
            return image.Frames.Count == 1
                && string.Equals(format.DefaultMimeType, contentType, StringComparison.OrdinalIgnoreCase);
        }
        catch (UnknownImageFormatException) { return false; }
        catch (InvalidImageContentException) { return false; }
    }
    private async Task QueueCleanupAsync(string objectKey, CancellationToken ct)
    {
        try { await cleanup.QueueAsync(objectKey, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* The object remains private; scheduled reconciliation will retry persistence. */ }
    }
}
