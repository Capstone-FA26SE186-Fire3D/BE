using Fire3D.Application.Storage;
using Fire3D.Domain.Entities;
using SkiaSharp;

namespace Fire3D.Application.Authentication.Avatar;

public sealed class AvatarService(IAuthStore authStore, IAvatarStore avatarStore, IAvatarCleanupStore cleanup, IStorageService storage, TimeProvider clock) : IAvatarService
{
    // Kept in the constructor to preserve the DI boundary: AvatarStore owns the durable reconciliation queue.
    private readonly IAvatarCleanupStore cleanupStore = cleanup;
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
            return AuthResult<AvatarResponse>.Fail("AVATAR_UPLOAD_UNAVAILABLE", "Avatar upload failed. Try again.", 503);
        }

        var completed = await CompleteUploadAsync(userId, expectedProfileRevision, new(intent.Id), ct);
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
        var reservation = await avatarStore.ReserveCopyCandidateAsync(intent.Id, userId, expectedProfileRevision, metadata.ETag, now, ct);
        if (reservation.Status == AvatarCandidateReservationStatus.PreconditionFailed)
            return AuthResult<AvatarResponse>.Fail("PRECONDITION_FAILED", "The profile changed. Reload it and retry.", 412);
        if (!reservation.Reserved)
            return AuthResult<AvatarResponse>.Fail("INVALID_AVATAR_UPLOAD", "Avatar upload is no longer available.", 409);
        var candidate = reservation.Candidate!;
        if (!await storage.CopyObjectIfUnchangedAsync(intent.StagingObjectKey, candidate.SourceEtag, candidate.ObjectKey, intent.ContentType, ct))
            return AuthResult<AvatarResponse>.Fail("AVATAR_UPLOAD_CHANGED", "Avatar upload changed before it could be completed. Upload again.", 409);
        var completedAt = UtcNow();
        var finalized = await avatarStore.FinalizeUploadAsync(intent.Id, userId, candidate.AttemptId, expectedProfileRevision, candidate.ObjectKey, completedAt, ct);
        if (!finalized.Finalized)
        {
            return finalized.Status == AvatarFinalizeStatus.PreconditionFailed
                ? AuthResult<AvatarResponse>.Fail("PRECONDITION_FAILED", "The profile changed. Reload it and retry.", 412)
                : AuthResult<AvatarResponse>.Fail("INVALID_AVATAR_UPLOAD", "Avatar upload is no longer available.", 409);
        }
        var url = await storage.GeneratePresignedDownloadUrlAsync(candidate.ObjectKey, UrlLifetime, ct);
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
        if (intent.ExpectedProfileRevision != expectedProfileRevision)
            return AuthResult<AvatarResponse>.Fail("PRECONDITION_FAILED", "The profile changed. Reload it and retry.", 412);
        if (string.IsNullOrWhiteSpace(intent.FinalObjectKey) || user.AvatarStorageKey != intent.FinalObjectKey)
            return AuthResult<AvatarResponse>.Fail("AVATAR_UPLOAD_SUPERSEDED", "This upload was replaced by a newer profile change.", 409);
        var now = UtcNow();
        var url = await storage.GeneratePresignedDownloadUrlAsync(intent.FinalObjectKey, UrlLifetime, ct);
        return AuthResult<AvatarResponse>.Ok(new(url, now.Add(UrlLifetime), user.ProfileRevision));
    }

    private static bool IsSafeImage(byte[]? bytes, string contentType)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length > AvatarUploadRules.MaxBytes) return false;
        using var input = new MemoryStream(bytes, writable: false);
        using var codec = SKCodec.Create(input);
        if (codec is null) return false;
        var mime = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Webp => "image/webp",
            _ => null
        };
        if (mime is null || !string.Equals(mime, contentType, StringComparison.OrdinalIgnoreCase)
            || codec.Info.Width is <= 0 or > 4096 || codec.Info.Height is <= 0 or > 4096
            || codec.FrameCount > 1
            || (codec.EncodedFormat == SKEncodedImageFormat.Png && !HasSinglePngFrame(bytes))) return false;

        // Bound decoded memory before allocation. Success is required: a partially
        // decoded/truncated image must not become a profile's immutable source.
        using var pixels = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul));
        return pixels.GetPixels() != IntPtr.Zero
            && codec.GetPixels(pixels.Info, pixels.GetPixels()) == SKCodecResult.Success;
    }

    private static bool HasSinglePngFrame(ReadOnlySpan<byte> bytes)
    {
        // Skia may decode an APNG's default image as static PNG. Inspect its
        // animation controls too, so unsupported animation cannot bypass FrameCount.
        var animation = false;
        var frames = 0;
        for (var offset = 8; offset <= bytes.Length - 12;)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > (uint)(bytes.Length - offset - 12)) return false;
            var type = bytes.Slice(offset + 4, 4);
            if (type.SequenceEqual("acTL"u8))
            {
                if (animation || length != 8
                    || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8, 4)) != 1) return false;
                animation = true;
            }
            else if (type.SequenceEqual("fcTL"u8))
            {
                if (!animation || length != 26 || ++frames > 1) return false;
            }
            else if (type.SequenceEqual("fdAT"u8)) return false; // An extra frame beyond the default image.
            offset += (int)length + 12;
            if (type.SequenceEqual("IEND"u8)) return length == 0 && offset == bytes.Length && (!animation || frames == 1);
        }
        return false;
    }
}
