using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Avatar;
using Fire3D.Application.Storage;
using Fire3D.Domain.Entities;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class AvatarServiceTests
{
    [Fact]
    public async Task Create_upload_intent_uses_a_server_owned_staging_key_and_a_five_minute_url()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "avatar@example.test", IsActive = true };
        var storage = new AvatarStorageFake();
        var service = new AvatarService(new AvatarAuthStoreFake(user), new AvatarStoreFake(), storage,
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
        var service = new AvatarService(new AvatarAuthStoreFake(user), avatarStore, storage, TimeProvider.System);
        var intent = await service.CreateUploadIntentAsync(user.Id, new("image/png", 1024), default);

        var result = await service.CompleteUploadAsync(user.Id, new(intent.Value!.UploadId), default);

        Assert.Equal("INVALID_AVATAR_CONTENT", result.Error?.Code);
        Assert.False(storage.Copied);
        Assert.False(avatarStore.Finalized);
    }

    [Fact]
    public async Task Complete_succeeds_when_post_commit_staging_cleanup_is_temporarily_unavailable()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "avatar@example.test", IsActive = true };
        var avatarStore = new AvatarStoreFake();
        var storage = new AvatarStorageFake { ThrowOnDelete = true };
        var service = new AvatarService(new AvatarAuthStoreFake(user), avatarStore, storage, TimeProvider.System);
        var intent = await service.CreateUploadIntentAsync(user.Id, new("image/png", 1024), default);

        var result = await service.CompleteUploadAsync(user.Id, new(intent.Value!.UploadId), default);

        Assert.True(result.IsSuccess);
        Assert.True(avatarStore.Finalized);
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
        public Task UpdateUserAsync(User user, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateLoginAsync(Guid id, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> UpsertDeviceAsync(Guid userId, string deviceUuid, string? fcmToken, string? deviceModel, string? osVersion, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeDeviceAsync(Guid userId, string deviceUuid, DateTime now, CancellationToken ct) => throw new NotSupportedException();
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
        public bool Finalized { get; private set; }
        public Task SaveUploadIntentAsync(AvatarUploadIntent intent, CancellationToken ct) { intents.Add(intent.Id, intent); return Task.CompletedTask; }
        public Task<AvatarUploadIntent?> FindUploadIntentAsync(Guid id, Guid userId, CancellationToken ct) => Task.FromResult(intents.GetValueOrDefault(id) is { UserId: var owner } intent && owner == userId ? intent : null);
        public Task<AvatarFinalizeResult> FinalizeUploadAsync(Guid intentId, Guid userId, string objectKey, DateTime completedAt, CancellationToken ct) { Finalized = true; return Task.FromResult(new AvatarFinalizeResult(true, null)); }
        public Task<AvatarDeleteResult> DeleteAvatarAsync(Guid userId, DateTime deletedAt, CancellationToken ct) => Task.FromResult(new AvatarDeleteResult(true, null));
    }

    private sealed class AvatarStorageFake : IStorageService
    {
        public string UploadedKey { get; private set; } = "";
        public string UploadContentType { get; private set; } = "";
        public TimeSpan UploadExpiration { get; private set; }
        public byte[] Prefix { get; init; } = [137, 80, 78, 71, 13, 10, 26, 10];
        public bool Copied { get; private set; }
        public bool ThrowOnDelete { get; init; }
        public Task<string> GeneratePresignedUploadUrlAsync(string objectKey, string mimeType, TimeSpan expiration, CancellationToken ct) { UploadedKey = objectKey; UploadContentType = mimeType; UploadExpiration = expiration; return Task.FromResult("https://storage.test/upload"); }
        public Task<bool> VerifyObjectExistsAsync(string objectKey, long expectedSizeBytes, CancellationToken ct) => Task.FromResult(true);
        public Task<StorageObjectMetadata?> GetObjectMetadataAsync(string objectKey, CancellationToken ct) => Task.FromResult<StorageObjectMetadata?>(new(1024, "image/png"));
        public Task<byte[]?> ReadObjectPrefixAsync(string objectKey, int length, CancellationToken ct) => Task.FromResult<byte[]?>(Prefix);
        public Task CopyObjectAsync(string sourceKey, string destinationKey, string contentType, CancellationToken ct) { Copied = true; return Task.CompletedTask; }
        public Task DeleteObjectAsync(string objectKey, CancellationToken ct) => ThrowOnDelete
            ? Task.FromException(new InvalidOperationException("temporary storage failure")) : Task.CompletedTask;
        public Task<string> GeneratePresignedDownloadUrlAsync(string objectKey, TimeSpan expiration, CancellationToken ct) => Task.FromResult("https://storage.test/download");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
