using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Avatar;
using Fire3D.Application.Storage;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using SkiaSharp;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class AvatarServiceTests
{
    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp")]
    public async Task Complete_decodes_each_supported_format(SKEncodedImageFormat format, string mime)
    {
        var bytes = CreateImageBytes(format, 2, 2);
        var (service, user, store, storage) = ImageFixture(bytes, mime);
        var upload = await service.CreateUploadIntentAsync(user.Id, new(mime, bytes.Length), default);
        var result = await service.CompleteUploadAsync(user.Id, 1, new(upload.Value!.UploadId), default);
        Assert.True(result.IsSuccess);
        Assert.True(store.Finalized);
        Assert.True(storage.Copied);
    }

    [Theory]
    [InlineData(4097, 1)]
    [InlineData(1, 4097)]
    public async Task Complete_rejects_oversized_dimensions_before_copy(int width, int height)
    {
        var bytes = CreateImageBytes(SKEncodedImageFormat.Png, width, height);
        var (service, user, store, storage) = ImageFixture(bytes, "image/png");
        var upload = await service.CreateUploadIntentAsync(user.Id, new("image/png", bytes.Length), default);
        var result = await service.CompleteUploadAsync(user.Id, 1, new(upload.Value!.UploadId), default);
        Assert.Equal("INVALID_AVATAR_CONTENT", result.Error?.Code);
        Assert.False(store.Finalized);
        Assert.False(storage.Copied);
    }

    [Fact]
    public async Task Complete_rejects_truncated_pixels_even_with_valid_header_and_matching_metadata()
    {
        var bytes = CreateImageBytes(SKEncodedImageFormat.Png, 16, 16)[..45];
        var (service, user, store, storage) = ImageFixture(bytes, "image/png");
        var upload = await service.CreateUploadIntentAsync(user.Id, new("image/png", bytes.Length), default);
        var result = await service.CompleteUploadAsync(user.Id, 1, new(upload.Value!.UploadId), default);
        Assert.Equal("INVALID_AVATAR_CONTENT", result.Error?.Code);
        Assert.False(store.Finalized);
        Assert.False(storage.Copied);
    }

    private static (AvatarService Service, User User, AvatarStoreFake Store, AvatarStorageFake Storage)
        ImageFixture(byte[] bytes, string mime)
    {
        var user = new User { Id = Guid.NewGuid(), Email = "avatar@example.test", IsActive = true };
        var store = new AvatarStoreFake();
        var storage = new AvatarStorageFake { Bytes = bytes, Prefix = bytes.Take(12).ToArray(), MetadataMime = mime, MetadataSize = bytes.Length };
        return (new AvatarService(new AvatarAuthStoreFake(user), store, new AvatarCleanupStoreFake(), storage, TimeProvider.System), user, store, storage);
    }

    [Fact]
    public async Task Complete_rejects_multiple_png_frames()
    {
        var png = CreatePngBytes();
        using var animated = new MemoryStream();
        animated.Write(png.AsSpan(0, 33)); // Signature and IHDR.
        WritePngChunk(animated, "acTL", [0, 0, 0, 2, 0, 0, 0, 0]);
        byte[] frame = [0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 10, 0, 0];
        WritePngChunk(animated, "fcTL", frame);
        using var compressed = new MemoryStream();
        for (var offset = 33; offset < png.Length;)
        {
            var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IDAT")
            {
                animated.Write(png.AsSpan(offset, length + 12));
                compressed.Write(png.AsSpan(offset + 8, length));
            }
            offset += length + 12;
        }
        frame[3] = 1;
        WritePngChunk(animated, "fcTL", frame);
        WritePngChunk(animated, "fdAT", [0, 0, 0, 2, .. compressed.ToArray()]);
        WritePngChunk(animated, "IEND", []);
        var bytes = animated.ToArray();
        var (service, user, store, storage) = ImageFixture(bytes, "image/png");
        var upload = await service.CreateUploadIntentAsync(user.Id, new("image/png", bytes.Length), default);
        var result = await service.CompleteUploadAsync(user.Id, 1, new(upload.Value!.UploadId), default);
        Assert.Equal("INVALID_AVATAR_CONTENT", result.Error?.Code);
        Assert.False(store.Finalized);
        Assert.False(storage.Copied);
    }

    [Fact]
    public async Task Complete_checks_decoded_mime_even_when_signature_and_metadata_claim_png()
    {
        var jpeg = CreateImageBytes(SKEncodedImageFormat.Jpeg, 2, 2);
        var (service, user, store, _) = ImageFixture(jpeg, "image/png");
        var storage = new AvatarStorageFake { Bytes = jpeg, MetadataSize = jpeg.Length };
        service = new AvatarService(new AvatarAuthStoreFake(user), store, new AvatarCleanupStoreFake(), storage, TimeProvider.System);
        var upload = await service.CreateUploadIntentAsync(user.Id, new("image/png", jpeg.Length), default);
        var result = await service.CompleteUploadAsync(user.Id, 1, new(upload.Value!.UploadId), default);
        Assert.Equal("INVALID_AVATAR_CONTENT", result.Error?.Code);
        Assert.False(store.Finalized);
        Assert.False(storage.Copied);
    }

    private static void WritePngChunk(Stream output, string type, byte[] payload)
    {
        Span<byte> number = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, (uint)payload.Length);
        output.Write(number);
        byte[] body = [.. System.Text.Encoding.ASCII.GetBytes(type), .. payload];
        output.Write(body);
        uint crc = uint.MaxValue;
        foreach (var value in body)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0xedb88320U);
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        output.Write(number);
    }

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
        intent.ExpectedProfileRevision = 1;
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
        intent.ExpectedProfileRevision = 1;
        user.AvatarStorageKey = intent.FinalObjectKey;

        var result = await service.CompleteUploadAsync(user.Id, 1, new(upload.Value!.UploadId), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(9, result.Value!.ProfileRevision);
    }

    [Fact]
    public async Task Complete_replay_requires_the_original_profile_revision()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "avatar@example.test", IsActive = true, ProfileRevision = 2 };
        var avatarStore = new AvatarStoreFake();
        var service = new AvatarService(new AvatarAuthStoreFake(user), avatarStore, new AvatarCleanupStoreFake(), new AvatarStorageFake(), TimeProvider.System);
        var upload = await service.CreateUploadIntentAsync(user.Id, new("image/png", 1024), default);
        var intent = Assert.IsType<AvatarUploadIntent>(avatarStore.LastIntent);
        intent.CompletedAt = DateTime.UtcNow;
        intent.FinalObjectKey = "avatars/users/replayed";
        intent.ExpectedProfileRevision = 1;
        user.AvatarStorageKey = intent.FinalObjectKey;

        var result = await service.CompleteUploadAsync(user.Id, 2, new(upload.Value!.UploadId), default);

        Assert.Equal("PRECONDITION_FAILED", result.Error?.Code);
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
        Assert.Equal(storage.CopiedKeys[0], storage.CopiedKeys[1]);
        Assert.DoesNotContain(storage.CopiedKeys[0], storage.DeletedKeys);
        Assert.Empty(cleanup.QueuedKeys);
    }

    private static (AvatarService Service, User User, AvatarStoreFake Store, AvatarStorageFake Storage) Fixture(
        AvatarStoreFake? store = null, AvatarStorageFake? storage = null, DateTimeOffset? now = null)
    {
        var user = new User { Id = Guid.NewGuid(), Email = "avatar@example.test", IsActive = true };
        store ??= new AvatarStoreFake();
        storage ??= new AvatarStorageFake();
        TimeProvider clock = now is null ? TimeProvider.System : new FixedTimeProvider(now.Value);
        return (new AvatarService(new AvatarAuthStoreFake(user), store, new AvatarCleanupStoreFake(), storage, clock), user, store, storage);
    }

    [Fact]
    public async Task Upload_file_requires_a_revision_and_content_before_creating_an_intent()
    {
        var (service, user, store, storage) = Fixture();
        Assert.Equal(400, (await service.UploadFileAsync(user.Id, 0, "image/png", 1, new MemoryStream([1]), default)).Error?.Status);
        var missing = await service.UploadFileAsync(user.Id, 1, "image/png", 1, null!, default);
        Assert.Equal(("VALIDATION_ERROR", 400), (missing.Error?.Code, missing.Error?.Status));
        Assert.Null(store.LastIntent);
        Assert.False(storage.UploadedDirectly);
    }

    [Fact]
    public async Task Upload_file_returns_intent_validation_errors_unchanged()
    {
        var (service, user, store, _) = Fixture();
        var result = await service.UploadFileAsync(user.Id, 1, "image/gif", 10, new MemoryStream(new byte[10]), default);
        Assert.Equal(400, result.Error?.Status);
        Assert.Null(store.LastIntent);
    }

    [Fact]
    public async Task Upload_file_is_unavailable_when_the_saved_intent_cannot_be_reloaded()
    {
        var (service, user, _, storage) = Fixture(new AvatarStoreFake { LoseIntents = true });
        var result = await service.UploadFileAsync(user.Id, 1, "image/png", 3, new MemoryStream([1, 2, 3]), default);
        Assert.Equal(("AVATAR_UPLOAD_UNAVAILABLE", 503), (result.Error?.Code, result.Error?.Status));
        Assert.False(storage.UploadedDirectly);
    }

    [Fact]
    public async Task Upload_file_maps_storage_failure_to_503_without_completing()
    {
        var (service, user, store, _) = Fixture(storage: new AvatarStorageFake { UploadFailure = new InvalidOperationException("s3 down") });
        var result = await service.UploadFileAsync(user.Id, 1, "image/png", 3, new MemoryStream([1, 2, 3]), default);
        Assert.Equal(("AVATAR_UPLOAD_UNAVAILABLE", 503), (result.Error?.Code, result.Error?.Status));
        Assert.False(store.Finalized);
    }

    [Fact]
    public async Task Upload_file_propagates_cancellation_from_storage()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var (service, user, store, _) = Fixture(storage: new AvatarStorageFake { UploadFailure = new OperationCanceledException(cancelled.Token) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.UploadFileAsync(user.Id, 1, "image/png", 3, new MemoryStream([1, 2, 3]), cancelled.Token));
        Assert.False(store.Finalized);
    }

    [Fact]
    public async Task Complete_rejects_unknown_expired_and_stale_completed_intents()
    {
        var now = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        var (service, user, store, storage) = Fixture(now: now);
        var unknown = await service.CompleteUploadAsync(user.Id, 1, new(Guid.NewGuid()), default);
        Assert.Equal(("INVALID_AVATAR_UPLOAD", 409), (unknown.Error?.Code, unknown.Error?.Status));

        var upload = await service.CreateUploadIntentAsync(user.Id, new("image/png", 1024), default);
        store.LastIntent!.ExpiresAt = now.UtcDateTime;
        var expired = await service.CompleteUploadAsync(user.Id, 1, new(upload.Value!.UploadId), default);
        Assert.Equal(("INVALID_AVATAR_UPLOAD", 409), (expired.Error?.Code, expired.Error?.Status));
        Assert.Contains("expired", expired.Error!.Message);

        store.LastIntent.CompletedAt = now.UtcDateTime.AddHours(-24).AddSeconds(-1);
        var stale = await service.CompleteUploadAsync(user.Id, 1, new(upload.Value.UploadId), default);
        Assert.Equal(("INVALID_AVATAR_UPLOAD", 409), (stale.Error?.Code, stale.Error?.Status));
        Assert.Contains("replay window", stale.Error!.Message);
        Assert.Empty(storage.CopiedKeys);
    }

    [Fact]
    public async Task Complete_rejects_unavailable_accounts_and_missing_upload_ids()
    {
        var (service, user, _, _) = Fixture();
        Assert.Equal(400, (await service.CompleteUploadAsync(user.Id, 1, new(Guid.Empty), default)).Error?.Status);
        user.IsActive = false;
        var result = await service.CompleteUploadAsync(user.Id, 1, new(Guid.NewGuid()), default);
        Assert.Equal(("UNAUTHORIZED", 401), (result.Error?.Code, result.Error?.Status));
    }

    [Theory]
    [InlineData(2048L, "image/png")]
    [InlineData(1024L, "image/jpeg")]
    public async Task Complete_rejects_metadata_that_differs_from_the_intent(long size, string mime)
    {
        var (service, user, store, storage) = Fixture(storage: new AvatarStorageFake { MetadataSize = size, MetadataMime = mime });
        var upload = await service.CreateUploadIntentAsync(user.Id, new("image/png", 1024), default);
        var result = await service.CompleteUploadAsync(user.Id, 1, new(upload.Value!.UploadId), default);
        Assert.Equal(("INVALID_AVATAR_UPLOAD", 400), (result.Error?.Code, result.Error?.Status));
        Assert.False(storage.Copied);
        Assert.False(store.Finalized);
    }

    [Theory]
    [InlineData(AvatarCandidateReservationStatus.PreconditionFailed, "PRECONDITION_FAILED", 412)]
    [InlineData(AvatarCandidateReservationStatus.Unavailable, "INVALID_AVATAR_UPLOAD", 409)]
    public async Task Complete_maps_a_failed_copy_reservation_without_copying(AvatarCandidateReservationStatus status, string code, int http)
    {
        var (service, user, store, storage) = Fixture(new AvatarStoreFake { ReservationStatus = status });
        var upload = await service.CreateUploadIntentAsync(user.Id, new("image/png", 1024), default);
        var result = await service.CompleteUploadAsync(user.Id, 1, new(upload.Value!.UploadId), default);
        Assert.Equal((code, http), (result.Error?.Code, result.Error?.Status));
        Assert.False(storage.Copied);
        Assert.False(store.Finalized);
    }

    [Fact]
    public async Task Complete_replay_reports_a_superseded_upload_when_the_profile_points_elsewhere()
    {
        var (service, user, store, storage) = Fixture();
        var upload = await service.CreateUploadIntentAsync(user.Id, new("image/png", 1024), default);
        store.LastIntent!.CompletedAt = DateTime.UtcNow;
        store.LastIntent.FinalObjectKey = "avatars/users/older";
        store.LastIntent.ExpectedProfileRevision = 1;
        user.AvatarStorageKey = "avatars/users/newer";
        var result = await service.CompleteUploadAsync(user.Id, 1, new(upload.Value!.UploadId), default);
        Assert.Equal(("AVATAR_UPLOAD_SUPERSEDED", 409), (result.Error?.Code, result.Error?.Status));
        Assert.Empty(storage.CopiedKeys);
    }

    [Fact]
    public async Task Complete_rejects_a_decodable_image_in_an_unsupported_format()
    {
        // A decodable 1x1 24-bit BMP passes the fake PNG prefix check but must fail the decoded-format check.
        byte[] bmp = [(byte)'B', (byte)'M', 58, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0,
            40, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 24, 0, 0, 0, 0, 0, 4, 0, 0, 0,
            0x13, 0x0B, 0, 0, 0x13, 0x0B, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 255, 0];
        using (var codec = SKCodec.Create(new MemoryStream(bmp))) Assert.Equal(SKEncodedImageFormat.Bmp, codec?.EncodedFormat);
        var (service, user, store, storage) = ImageFixture(bmp, "image/png");
        var upload = await service.CreateUploadIntentAsync(user.Id, new("image/png", bmp.Length), default);
        var result = await service.CompleteUploadAsync(user.Id, 1, new(upload.Value!.UploadId), default);
        Assert.Equal("INVALID_AVATAR_CONTENT", result.Error?.Code);
        Assert.False(storage.Copied);
        Assert.False(store.Finalized);
    }

    [Fact]
    public async Task Complete_accepts_an_apng_that_declares_exactly_one_frame()
    {
        var png = CreatePngBytes();
        using var animated = new MemoryStream();
        animated.Write(png.AsSpan(0, 33));
        WritePngChunk(animated, "acTL", [0, 0, 0, 1, 0, 0, 0, 0]);
        WritePngChunk(animated, "fcTL", [0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 10, 0, 0]);
        animated.Write(png.AsSpan(33));
        var bytes = animated.ToArray();
        var (service, user, store, storage) = ImageFixture(bytes, "image/png");
        var upload = await service.CreateUploadIntentAsync(user.Id, new("image/png", bytes.Length), default);
        var result = await service.CompleteUploadAsync(user.Id, 1, new(upload.Value!.UploadId), default);
        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.True(storage.Copied);
        Assert.True(store.Finalized);
    }

    [Fact]
    public async Task Complete_rejects_a_png_without_an_end_chunk()
    {
        var png = CreatePngBytes();
        var bytes = png[..^12]; // Drop IEND.
        var (service, user, store, storage) = ImageFixture(bytes, "image/png");
        var upload = await service.CreateUploadIntentAsync(user.Id, new("image/png", bytes.Length), default);
        var result = await service.CompleteUploadAsync(user.Id, 1, new(upload.Value!.UploadId), default);
        Assert.Equal("INVALID_AVATAR_CONTENT", result.Error?.Code);
        Assert.False(storage.Copied);
        Assert.False(store.Finalized);
    }

    [Fact]
    public async Task Get_avatar_returns_404_until_set_then_a_five_minute_url_with_the_profile_revision()
    {
        var now = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
        var (service, user, _, _) = Fixture(now: now);
        var missing = await service.GetAvatarAsync(user.Id, default);
        Assert.Equal(("AVATAR_NOT_FOUND", 404), (missing.Error?.Code, missing.Error?.Status));
        user.AvatarStorageKey = "avatars/users/current";
        user.ProfileRevision = 7;
        var result = await service.GetAvatarAsync(user.Id, default);
        Assert.Equal(new AvatarResponse("https://storage.test/download", now.UtcDateTime.AddMinutes(5), 7), result.Value);
        user.IsActive = false;
        Assert.Equal(401, (await service.GetAvatarAsync(user.Id, default)).Error?.Status);
    }

    [Theory]
    [InlineData(AvatarDeleteStatus.Deleted, null, null)]
    [InlineData(AvatarDeleteStatus.PreconditionFailed, "PRECONDITION_FAILED", 412)]
    [InlineData(AvatarDeleteStatus.Unavailable, "UNAUTHORIZED", 401)]
    public async Task Delete_avatar_maps_store_outcomes(AvatarDeleteStatus status, string? code, int? http)
    {
        var (service, user, _, _) = Fixture(new AvatarStoreFake { DeleteStatus = status });
        var result = await service.DeleteAvatarAsync(user.Id, 3, default);
        Assert.Equal((code, http), (result.Error?.Code, result.Error?.Status));
        Assert.Equal(code is null, result.IsSuccess);
    }

    [Fact]
    public async Task Delete_avatar_validates_revision_and_account_before_the_store()
    {
        var store = new AvatarStoreFake();
        var (service, user, _, _) = Fixture(store);
        var invalid = await service.DeleteAvatarAsync(user.Id, 0, default);
        Assert.Equal(("VALIDATION_ERROR", 400), (invalid.Error?.Code, invalid.Error?.Status));
        user.DeletedAt = DateTime.UtcNow;
        var deleted = await service.DeleteAvatarAsync(user.Id, 1, default);
        Assert.Equal(("UNAUTHORIZED", 401), (deleted.Error?.Code, deleted.Error?.Status));
        Assert.Equal(0, store.DeleteCalls);
    }

    private sealed class AvatarAuthStoreFake(User user) : IAuthStore
    {
        public Task<User?> FindUserAsync(Guid id, CancellationToken ct) => Task.FromResult<User?>(id == user.Id ? user : null);
        public User? ValidatedSessionActor(Guid userId) => null;
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
        public Task FinalizePasswordLoginAsync(User user,string? rehashedPassword,RefreshToken token,DateTime now,CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> SessionIsValidAsync(Guid userId,Guid familyId,string? role,string? organizationId,DateTime now,CancellationToken ct) => throw new NotSupportedException();
        public Task<DeviceRegistrationResult> RegisterDeviceAsync(Guid userId, string deviceUuid, Fire3D.Application.Users.Commands.RegisterDevice.DeviceInstallationProof installationProof, string? fcmToken, string? deviceModel, string? osVersion, string? appVersion, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeviceRevokeResult> RevokeDeviceAsync(Guid userId, string deviceUuid, Fire3D.Application.Users.Commands.RegisterDevice.DeviceInstallationProof installationProof, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task DisableUserPushDevicesAsync(Guid userId, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task<RefreshToken?> FindRefreshTokenAsync(string hash, CancellationToken ct) => throw new NotSupportedException();
        public Task AddRefreshTokenAsync(RefreshToken token, CancellationToken ct) => throw new NotSupportedException();
        public Task ConsumeRefreshTokenAsync(Guid id, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeFamilyAsync(Guid userId, Guid familyId, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeAllUserSessionsAsync(Guid userId, DateTime revokedAt, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> FamilyIsActiveAsync(Guid userId, Guid familyId, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task WriteAuditAsync(User actor, string action, Guid targetId, DateTime now, CancellationToken ct, Guid? correlationId = null) => throw new NotSupportedException();
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
        public AvatarCandidateReservationStatus? ReservationStatus { get; init; }
        public AvatarDeleteStatus DeleteStatus { get; init; } = AvatarDeleteStatus.Deleted;
        public bool LoseIntents { get; init; }
        public int DeleteCalls { get; private set; }
        public Task SaveUploadIntentAsync(AvatarUploadIntent intent, CancellationToken ct) { intents.Add(intent.Id, intent); LastIntent = intent; return Task.CompletedTask; }
        public Task<AvatarUploadIntent?> FindUploadIntentAsync(Guid id, Guid userId, CancellationToken ct) => Task.FromResult(!LoseIntents && intents.GetValueOrDefault(id) is { UserId: var owner } intent && owner == userId ? intent : null);
        public Task<AvatarCandidateReservation> ReserveCopyCandidateAsync(Guid intentId, Guid userId, long expectedProfileRevision, string sourceEtag, DateTime now, CancellationToken ct)
        {
            if (ReservationStatus is { } forced && forced != AvatarCandidateReservationStatus.Reserved)
                return Task.FromResult(new AvatarCandidateReservation(forced, null));
            var intent = intents[intentId];
            if (intent.CandidateAttemptId.HasValue)
                return Task.FromResult(new AvatarCandidateReservation(AvatarCandidateReservationStatus.Reserved,
                    new(intent.CandidateAttemptId.Value, intent.CandidateObjectKey!, intent.CandidateSourceEtag!, intent.CandidateLeaseUntil!.Value)));
            intent.ExpectedProfileRevision = expectedProfileRevision;
            intent.CandidateAttemptId = Guid.NewGuid();
            intent.CandidateObjectKey = "avatars/users/candidate-" + intent.CandidateAttemptId.Value.ToString("N");
            intent.CandidateSourceEtag = sourceEtag;
            intent.CandidateLeaseUntil = now.AddMinutes(15);
            return Task.FromResult(new AvatarCandidateReservation(AvatarCandidateReservationStatus.Reserved,
                new(intent.CandidateAttemptId.Value, intent.CandidateObjectKey, intent.CandidateSourceEtag, intent.CandidateLeaseUntil.Value)));
        }
        public Task<AvatarFinalizeResult> FinalizeUploadAsync(Guid intentId, Guid userId, Guid attemptId, long expectedProfileRevision, string objectKey, DateTime completedAt, CancellationToken ct)
        {
            var status = FinalizeStatuses[Math.Min(finalizeCount++, FinalizeStatuses.Length - 1)];
            Finalized |= status == AvatarFinalizeStatus.Finalized;
            return Task.FromResult(new AvatarFinalizeResult(status, null));
        }
        public Task<AvatarDeleteResult> DeleteAvatarAsync(Guid userId, long expectedProfileRevision, DateTime deletedAt, CancellationToken ct)
        {
            DeleteCalls++;
            return Task.FromResult(new AvatarDeleteResult(DeleteStatus, null));
        }
    }

    private sealed class AvatarStorageFake : IStorageService
    {
        public string UploadedKey { get; private set; } = "";
        public string UploadContentType { get; private set; } = "";
        public TimeSpan UploadExpiration { get; private set; }
        public byte[] Prefix { get; init; } = [137, 80, 78, 71, 13, 10, 26, 10];
        public byte[]? Bytes { get; init; }
        public string MetadataMime { get; init; } = "image/png";
        public long? MetadataSize { get; init; }
        public bool Copied { get; private set; }
        public bool UploadedDirectly { get; private set; }
        public long UploadedLength { get; private set; }
        public bool ThrowOnDelete { get; init; }
        public bool CopySucceeds { get; init; } = true;
        public List<string> CopiedKeys { get; } = [];
        public List<string> DeletedKeys { get; } = [];
        public Exception? UploadFailure { get; init; }
        public Task UploadObjectAsync(string objectKey, Stream content, long contentLength, string contentType, CancellationToken ct)
        {
            if (UploadFailure is not null) return Task.FromException(UploadFailure);
            UploadedDirectly = true;
            UploadedLength = contentLength;
            UploadedKey = objectKey;
            UploadContentType = contentType;
            return Task.CompletedTask;
        }
        public Task<string> GeneratePresignedUploadUrlAsync(string objectKey, string mimeType, TimeSpan expiration, CancellationToken ct) { UploadedKey = objectKey; UploadContentType = mimeType; UploadExpiration = expiration; return Task.FromResult("https://storage.test/upload"); }
        public Task<bool> VerifyObjectExistsAsync(string objectKey, long expectedSizeBytes, CancellationToken ct) => Task.FromResult(true);
        public Task<StorageObjectMetadata?> GetObjectMetadataAsync(string objectKey, CancellationToken ct) => Task.FromResult<StorageObjectMetadata?>(new(MetadataSize ?? (UploadedLength == 0 ? 1024 : UploadedLength), MetadataMime, "etag"));
        public Task<byte[]?> ReadObjectPrefixAsync(string objectKey, int length, string expectedETag, CancellationToken ct) => Task.FromResult<byte[]?>(Prefix);
        public Task<byte[]?> ReadObjectAsync(string objectKey, long maxBytes, string expectedETag, CancellationToken ct) =>
            Task.FromResult<byte[]?>(Bytes ?? CreatePngBytes());
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
        public Task QueueAsync(string objectKey, DateTime availableAt, CancellationToken ct) { QueuedKeys.Add(objectKey); return Task.CompletedTask; }
        public Task<AvatarCleanupJob?> ClaimAsync(CancellationToken ct) => Task.FromResult<AvatarCleanupJob?>(null);
        public Task<bool> RenewAsync(AvatarCleanupJob job, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> IsReferencedAsync(string objectKey, CancellationToken ct) => Task.FromResult(false);
        public Task CompleteAsync(AvatarCleanupJob job, CancellationToken ct) => Task.CompletedTask;
        public Task FailAsync(AvatarCleanupJob job, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static byte[] CreatePngBytes() => CreateImageBytes(SKEncodedImageFormat.Png, 1, 1);

    private static byte[] CreateImageBytes(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Transparent);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 100);
        return data.ToArray();
    }
}
