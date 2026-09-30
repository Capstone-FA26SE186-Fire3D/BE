using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Avatar;
using Fire3D.Application.Storage;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class AvatarServiceTests
{
    [Fact]
    public async Task Create_upload_intent_uses_a_server_owned_staging_key_and_a_five_minute_url()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "avatar@example.test", IsActive = true };
        var storage = new AvatarStorageFake();
        var service = new AvatarService(new AvatarAuthStoreFake(user), new AvatarStoreFake(), new AvatarCleanupStoreFake(), storage,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero)));

        var result = await service.CreateUploadIntentAsync(user.Id, new("image/png", 1024), default);

        Assert.True(result.IsSuccess);
        Assert.Contains($"avatars/staging/{user.Id:N}/", storage.UploadedKey);
        Assert.Equal(TimeSpan.FromMinutes(5), storage.UploadExpiration);
        Assert.Equal("image/png", storage.UploadContentType);
    }

    [Fact]
    public async Task Complete_rejects_a_file_whose_bytes_do_not_match_the_declared_image_type()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "avatar@example.test", IsActive = true };
        var avatarStore = new AvatarStoreFake();
        var storage = new AvatarStorageFake { Prefix = [0xFF, 0xD8, 0xFF] };
        var service = new AvatarService(new AvatarAuthStoreFake(user), avatarStore, new AvatarCleanupStoreFake(), storage, TimeProvider.System);
        var intent = await service.CreateUploadIntentAsync(user.Id, new("image/png", 1024), default);

        var result = await service.CompleteUploadAsync(user.Id, 1, new(intent.Value!.UploadId), default);

        Assert.Equal("INVALID_AVATAR_CONTENT", result.Error?.Code);
        Assert.False(storage.Copied);
        Assert.False(avatarStore.Finalized);
    }

    [Fact]
    public async Task Upload_file_streams_to_the_server_owned_staging_key_then_completes()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "avatar@example.test", IsActive = true };
        var avatarStore = new AvatarStoreFake();
        var storage = new AvatarStorageFake();
        var service = new AvatarService(new AvatarAuthStoreFake(user), avatarStore, new AvatarCleanupStoreFake(), storage, TimeProvider.System);

        var image = CreatePngBytes();
        await using var file = new MemoryStream(image);
        var result = await service.UploadFileAsync(user.Id, 1, "image/png", file.Length, file, default);

        Assert.True(result.IsSuccess);
        Assert.True(storage.UploadedDirectly);
        Assert.True(avatarStore.Finalized);
    }

    [Fact]
    public async Task Complete_succeeds_when_post_commit_staging_cleanup_is_temporarily_unavailable()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "avatar@example.test", IsActive = true };
        var avatarStore = new AvatarStoreFake();
        var storage = new AvatarStorageFake { ThrowOnDelete = true };
        var service = new AvatarService(new AvatarAuthStoreFake(user), avatarStore, new AvatarCleanupStoreFake(), storage, TimeProvider.System);
        var intent = await service.CreateUploadIntentAsync(user.Id, new("image/png", 1024), default);

        var result = await service.CompleteUploadAsync(user.Id, 1, new(intent.Value!.UploadId), default);

        Assert.True(result.IsSuccess);
        Assert.True(avatarStore.Finalized);
    }

    [Fact]
    public async Task Complete_rejects_when_the_inspected_s3_object_changes_before_copy()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "avatar@example.test", IsActive = true };
        var avatarStore = new AvatarStoreFake();
        var storage = new AvatarStorageFake { CopySucceeds = false };
        var service = new AvatarService(new AvatarAuthStoreFake(user), avatarStore, new AvatarCleanupStoreFake(), storage, TimeProvider.System);
        var intent = await service.CreateUploadIntentAsync(user.Id, new("image/png", 1024), default);

        var result = await service.CompleteUploadAsync(user.Id, 1, new(intent.Value!.UploadId), default);

        Assert.Equal("AVATAR_UPLOAD_CHANGED", result.Error?.Code);
        Assert.False(avatarStore.Finalized);
    }

    [Fact]
    public async Task Complete_replays_a_completed_upload_when_the_profile_still_points_to_its_final_object()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "avatar@example.test", IsActive = true };
        var avatarStore = new AvatarStoreFake();
        var storage = new AvatarStorageFake();
        var service = new AvatarService(new AvatarAuthStoreFake(user), avatarStore, new AvatarCleanupStoreFake(), storage, TimeProvider.System);
        var upload = await service.CreateUploadIntentAsync(user.Id, new("image/png", 1024), default);
        var intent = Assert.IsType<AvatarUploadIntent>(avatarStore.LastIntent);
        intent.CompletedAt = DateTime.UtcNow;
        intent.FinalObjectKey = "avatars/users/replayed";
        user.AvatarStorageKey = intent.FinalObjectKey;
        user.ProfileRevision = 2;

        var result = await service.CompleteUploadAsync(user.Id, 1, new(upload.Value!.UploadId), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.ProfileRevision);
        Assert.Empty(storage.CopiedKeys);
    }

    [Fact]
    public async Task Complete_replays_after_the_upload_url_expires_when_the_avatar_is_still_current()
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var user = new User { Id = Guid.NewGuid(), Email = "avatar@example.test", IsActive = true, ProfileRevision = 9 };
        var avatarStore = new AvatarStoreFake();
        var service = new AvatarService(new AvatarAuthStoreFake(user), avatarStore, new AvatarCleanupStoreFake(), new AvatarStorageFake(), new FixedTimeProvider(now));
        var upload = await service.CreateUploadIntentAsync(user.Id, new("image/png", 1024), default);
        var intent = Assert.IsType<AvatarUploadIntent>(avatarStore.LastIntent);
        intent.ExpiresAt = now.UtcDateTime.AddMinutes(-1);
        intent.CompletedAt = now.UtcDateTime.AddMinutes(-10);
        intent.FinalObjectKey = "avatars/users/replayed-after-expiry";
        user.AvatarStorageKey = intent.FinalObjectKey;

        var result = await service.CompleteUploadAsync(user.Id, 1, new(upload.Value!.UploadId), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(9, result.Value!.ProfileRevision);
    }

    [Fact]
    public async Task Losing_complete_attempt_never_deletes_the_winning_final_object()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "avatar@example.test", IsActive = true };
        var avatarStore = new AvatarStoreFake { FinalizeStatuses = [AvatarFinalizeStatus.Finalized, AvatarFinalizeStatus.PreconditionFailed] };
        var storage = new AvatarStorageFake();
        var cleanup = new AvatarCleanupStoreFake();
        var service = new AvatarService(new AvatarAuthStoreFake(user), avatarStore, cleanup, storage, TimeProvider.System);
        var intent = await service.CreateUploadIntentAsync(user.Id, new("image/png", 1024), default);

        Assert.True((await service.CompleteUploadAsync(user.Id, 1, new(intent.Value!.UploadId), default)).IsSuccess);
        var loser = await service.CompleteUploadAsync(user.Id, 1, new(intent.Value!.UploadId), default);

        Assert.Equal("PRECONDITION_FAILED", loser.Error?.Code);
        Assert.Equal(2, storage.CopiedKeys.Count);
        Assert.NotEqual(storage.CopiedKeys[0], storage.CopiedKeys[1]);
        Assert.DoesNotContain(storage.CopiedKeys[0], storage.DeletedKeys);
        Assert.Contains(storage.CopiedKeys[1], cleanup.QueuedKeys);
    }

    private sealed class AvatarAuthStoreFake(User user) : IAuthStore
    {
        public Task<User?> FindUserAsync(Guid id, CancellationToken ct) => Task.FromResult<User?>(id == user.Id ? user : null);
        public Task<bool> OrganizationIsActiveAsync(Guid id, CancellationToken ct) => Task.FromResult(true);
        public Task<IAuthTransaction> BeginUserTransactionAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<User?> FindUserByEmailAsync(string email, CancellationToken ct) => throw new NotSupportedException();
        public Task<User?> FindUserByFirebaseUidAsync(string uid, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> HasAdminAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountActiveAdminsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> TryCreateUserAsync(User user, CancellationToken ct) => throw new NotSupportedException();
        public Task<RegisterConflict> TryCreateTraineeAsync(User user, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateUserAsync(User user, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdatePasswordHashAsync(Guid userId, string passwordHash, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProfileUpdateResult> UpdateProfileAsync(Guid userId, long expectedProfileRevision, string? fullName, string? username, DateOnly? dob, UserGender? gender, string? phoneNumber, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateLoginAsync(Guid id, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeviceRegistrationResult> RegisterDeviceAsync(Guid userId, string deviceUuid, string installationKeyHash, string? fcmToken, string? deviceModel, string? osVersion, string? appVersion, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeviceRevokeResult> RevokeDeviceAsync(Guid userId, string deviceUuid, string installationKeyHash, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task DisableUserPushDevicesAsync(Guid userId, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task<RefreshToken?> FindRefreshTokenAsync(string hash, CancellationToken ct) => throw new NotSupportedException();
        public Task AddRefreshTokenAsync(RefreshToken token, CancellationToken ct) => throw new NotSupportedException();
        public Task ConsumeRefreshTokenAsync(Guid id, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeFamilyAsync(Guid userId, Guid familyId, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeAllUserSessionsAsync(Guid userId, DateTime revokedAt, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> FamilyIsActiveAsync(Guid userId, Guid familyId, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task WriteAuditAsync(User actor, string action, Guid targetId, DateTime now, CancellationToken ct, Guid? correlationId = null) => throw new NotSupportedException();
        public Task SavePasswordResetTokenAsync(PasswordResetToken token, CancellationToken ct) => throw new NotSupportedException();
        public Task<PasswordResetToken?> FindValidResetTokenAsync(Guid tokenId, CancellationToken ct) => throw new NotSupportedException();
        public Task MarkResetTokenUsedAsync(Guid tokenId, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task InvalidateUserResetTokensAsync(Guid userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<RegisterConflict> TryCreateOrganizationWithUserAsync(Organization organization, User user, CancellationToken ct) => throw new NotSupportedException();
        public Task EnqueuePasswordResetAsync(string email, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class AvatarStoreFake : IAvatarStore
    {
        private readonly Dictionary<Guid, AvatarUploadIntent> intents = [];
        private int finalizeCount;
        public bool Finalized { get; private set; }
        public AvatarUploadIntent? LastIntent { get; private set; }
        public AvatarFinalizeStatus[] FinalizeStatuses { get; init; } = [AvatarFinalizeStatus.Finalized];
        public Task SaveUploadIntentAsync(AvatarUploadIntent intent, CancellationToken ct) { intents.Add(intent.Id, intent); LastIntent = intent; return Task.CompletedTask; }
        public Task<AvatarUploadIntent?> FindUploadIntentAsync(Guid id, Guid userId, CancellationToken ct) => Task.FromResult(intents.GetValueOrDefault(id) is { UserId: var owner } intent && owner == userId ? intent : null);
        public Task<AvatarFinalizeResult> FinalizeUploadAsync(Guid intentId, Guid userId, long expectedProfileRevision, string objectKey, DateTime completedAt, CancellationToken ct)
        {
            var status = FinalizeStatuses[Math.Min(finalizeCount++, FinalizeStatuses.Length - 1)];
            Finalized |= status == AvatarFinalizeStatus.Finalized;
            return Task.FromResult(new AvatarFinalizeResult(status, null));
        }
        public Task<AvatarDeleteResult> DeleteAvatarAsync(Guid userId, long expectedProfileRevision, DateTime deletedAt, CancellationToken ct) => Task.FromResult(new AvatarDeleteResult(AvatarDeleteStatus.Deleted, null));
    }

    private sealed class AvatarStorageFake : IStorageService
    {
        public string UploadedKey { get; private set; } = "";
        public string UploadContentType { get; private set; } = "";
        public TimeSpan UploadExpiration { get; private set; }
        public byte[] Prefix { get; init; } = [137, 80, 78, 71, 13, 10, 26, 10];
        public bool Copied { get; private set; }
        public bool UploadedDirectly { get; private set; }
        public long UploadedLength { get; private set; }
        public bool ThrowOnDelete { get; init; }
        public bool CopySucceeds { get; init; } = true;
        public List<string> CopiedKeys { get; } = [];
        public List<string> DeletedKeys { get; } = [];
        public Task UploadObjectAsync(string objectKey, Stream content, long contentLength, string contentType, CancellationToken ct)
        {
            UploadedDirectly = true;
            UploadedLength = contentLength;
            UploadedKey = objectKey;
            UploadContentType = contentType;
            return Task.CompletedTask;
        }
        public Task<string> GeneratePresignedUploadUrlAsync(string objectKey, string mimeType, TimeSpan expiration, CancellationToken ct) { UploadedKey = objectKey; UploadContentType = mimeType; UploadExpiration = expiration; return Task.FromResult("https://storage.test/upload"); }
        public Task<bool> VerifyObjectExistsAsync(string objectKey, long expectedSizeBytes, CancellationToken ct) => Task.FromResult(true);
        public Task<StorageObjectMetadata?> GetObjectMetadataAsync(string objectKey, CancellationToken ct) => Task.FromResult<StorageObjectMetadata?>(new(UploadedLength == 0 ? 1024 : UploadedLength, "image/png", "etag"));
        public Task<byte[]?> ReadObjectPrefixAsync(string objectKey, int length, string expectedETag, CancellationToken ct) => Task.FromResult<byte[]?>(Prefix);
        public Task<byte[]?> ReadObjectAsync(string objectKey, long maxBytes, string expectedETag, CancellationToken ct) =>
            Task.FromResult<byte[]?>(CreatePngBytes());
        public Task<bool> CopyObjectIfUnchangedAsync(string sourceKey, string sourceETag, string destinationKey, string contentType, CancellationToken ct)
        {
            Copied = true;
            CopiedKeys.Add(destinationKey);
            return Task.FromResult(CopySucceeds);
        }
        public Task DeleteObjectAsync(string objectKey, CancellationToken ct)
        {
            DeletedKeys.Add(objectKey);
            return ThrowOnDelete ? Task.FromException(new InvalidOperationException("temporary storage failure")) : Task.CompletedTask;
        }
        public Task<string> GeneratePresignedDownloadUrlAsync(string objectKey, TimeSpan expiration, CancellationToken ct) => Task.FromResult("https://storage.test/download");
    }

    private sealed class AvatarCleanupStoreFake : IAvatarCleanupStore
    {
        public List<string> QueuedKeys { get; } = [];
        public Task QueueAsync(string objectKey, CancellationToken ct) { QueuedKeys.Add(objectKey); return Task.CompletedTask; }
        public Task<AvatarCleanupJob?> ClaimAsync(CancellationToken ct) => Task.FromResult<AvatarCleanupJob?>(null);
        public Task<bool> IsReferencedAsync(string objectKey, CancellationToken ct) => Task.FromResult(false);
        public Task CompleteAsync(AvatarCleanupJob job, CancellationToken ct) => Task.CompletedTask;
        public Task FailAsync(AvatarCleanupJob job, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static byte[] CreatePngBytes()
    {
        using var image = new Image<Rgba32>(1, 1);
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }
}
