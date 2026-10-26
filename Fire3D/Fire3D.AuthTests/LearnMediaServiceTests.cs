using System.Security.Cryptography;
using Fire3D.Application.Content;
using Fire3D.Application.Storage;
using SkiaSharp;
using Xunit;

namespace Fire3D.AuthTests;

// BE#171: Learn image upload intent -> PUT -> complete, and the public image address.
public sealed class LearnMediaServiceTests
{
    private static readonly byte[] Png = EncodePng(3, 2);
    private static readonly Guid Admin = Guid.NewGuid();
    private const string Base = "https://api.example.test";

    private static byte[] EncodePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.OrangeRed);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static LearnImageUploadIntentRequest Request(byte[] bytes, string type = "image/png") => new("cover.png", type, bytes.Length, Sha(bytes));

    [Fact]
    public async Task Intent_validates_input_and_requires_an_idempotency_key()
    {
        var (service, _, _, _) = Create(Png);
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", (await service.CreateUploadIntentAsync(Admin, null, Request(Png), default)).Error?.Code);
        var invalid = await service.CreateUploadIntentAsync(Admin, "k", new("", "image/gif", LearnMediaRules.MaxBytes + 1, "xyz"), default);
        Assert.Equal(("VALIDATION_ERROR", 400), (invalid.Error?.Code, invalid.Error?.Status));
        Assert.Equal(["fileName", "contentType", "sizeBytes", "sha256"], invalid.Error!.Errors!.Keys);
    }

    [Fact]
    public async Task Intent_replays_the_same_key_and_rejects_other_input_under_it()
    {
        var (service, store, _, _) = Create(Png);
        var first = await service.CreateUploadIntentAsync(Admin, "cover-1", Request(Png), default);
        Assert.True(first.IsSuccess);
        Assert.Equal("image/png", first.Value!.Headers["Content-Type"]);
        Assert.Equal($"https://s3.test/put/learn/staging/{first.Value.UploadId:N}", first.Value.UploadUrl);
        Assert.Equal(first.Value.UploadId, (await service.CreateUploadIntentAsync(Admin, "cover-1", Request(Png), default)).Value!.UploadId);
        var other = await service.CreateUploadIntentAsync(Admin, "cover-1", new("other.png", "image/png", Png.Length, Sha(Png)), default);
        Assert.Equal(("IDEMPOTENCY_KEY_CONFLICT", 409), (other.Error?.Code, other.Error?.Status));
        Assert.Single(store.Rows);
    }

    [Fact]
    public async Task Complete_turns_a_verified_image_into_a_permanent_public_address_once()
    {
        var (service, store, storage, _) = Create(Png);
        var uploadId = (await service.CreateUploadIntentAsync(Admin, "cover-1", Request(Png), default)).Value!.UploadId;
        Assert.Equal("NOT_FOUND", (await service.SignedImageUrlAsync(uploadId, default)).Error?.Code);

        var done = await service.CompleteUploadAsync(Admin, uploadId, Base, default);
        Assert.True(done.IsSuccess, done.Error?.Message);
        Assert.Equal(new LearnImageResponse(uploadId, $"{Base}/api/learn/media/images/{uploadId}", 3, 2, "image/png", Png.Length), done.Value);
        Assert.Equal($"learn/images/{uploadId:N}", storage.Copied);
        Assert.Contains($"learn/staging/{uploadId:N}", storage.Deleted);
        Assert.Equal($"https://s3.test/get/learn/images/{uploadId:N}", (await service.SignedImageUrlAsync(uploadId, default)).Value);

        // Completing again (lost response, double click) returns the same image without a second copy or audit.
        storage.Copied = null;
        Assert.Equal(done.Value, (await service.CompleteUploadAsync(Admin, uploadId, Base, default)).Value);
        Assert.Null(storage.Copied);
        Assert.Equal(1, store.Completions);
        // Another admin cannot complete or learn about someone else's upload.
        Assert.Equal("NOT_FOUND", (await service.CompleteUploadAsync(Guid.NewGuid(), uploadId, Base, default)).Error?.Code);
    }

    [Fact]
    public async Task Complete_rejects_files_that_are_not_the_declared_image()
    {
        // Bytes that hash correctly but are not a PNG.
        var text = "not an image at all"u8.ToArray();
        var (service, _, _, _) = Create(text);
        var id = (await service.CreateUploadIntentAsync(Admin, "k", Request(text), default)).Value!.UploadId;
        Assert.Equal(("LEARN_IMAGE_INVALID", 422), Code(await service.CompleteUploadAsync(Admin, id, Base, default)));

        // A real PNG whose sha256 differs from the one declared.
        (service, _, _, _) = Create(Png);
        id = (await service.CreateUploadIntentAsync(Admin, "k", new("cover.png", "image/png", Png.Length, new string('0', 64)), default)).Value!.UploadId;
        Assert.Equal(("LEARN_IMAGE_INVALID", 422), Code(await service.CompleteUploadAsync(Admin, id, Base, default)));

        // A PNG declared as JPEG: S3 reports the declared type, the decoder finds PNG.
        (service, _, _, _) = Create(Png, storedType: "image/jpeg");
        id = (await service.CreateUploadIntentAsync(Admin, "k", Request(Png, "image/jpeg"), default)).Value!.UploadId;
        Assert.Equal(("LEARN_IMAGE_INVALID", 422), Code(await service.CompleteUploadAsync(Admin, id, Base, default)));

        // The object S3 holds differs in type from the intent.
        (service, _, _, _) = Create(Png, storedType: "image/webp");
        id = (await service.CreateUploadIntentAsync(Admin, "k", Request(Png), default)).Value!.UploadId;
        Assert.Equal(("VALIDATION_ERROR", 400), Code(await service.CompleteUploadAsync(Admin, id, Base, default)));
    }

    [Fact]
    public async Task Complete_reports_missing_expired_and_unavailable_uploads()
    {
        var (service, _, storage, time) = Create(Png);
        var id = (await service.CreateUploadIntentAsync(Admin, "k", Request(Png), default)).Value!.UploadId;
        storage.Missing = true;
        Assert.Equal(("LEARN_UPLOAD_MISSING", 409), Code(await service.CompleteUploadAsync(Admin, id, Base, default)));
        storage.Missing = false; storage.Down = true;
        Assert.Equal(("LEARN_MEDIA_UNAVAILABLE", 503), Code(await service.CompleteUploadAsync(Admin, id, Base, default)));
        Assert.Equal(("LEARN_MEDIA_UNAVAILABLE", 503), Code(await service.CreateUploadIntentAsync(Admin, "k2", Request(Png), default)));
        storage.Down = false;
        time.Now += LearnMediaRules.UploadLifetime;
        Assert.Equal(("LEARN_UPLOAD_EXPIRED", 409), Code(await service.CompleteUploadAsync(Admin, id, Base, default)));
        Assert.Equal(("LEARN_UPLOAD_EXPIRED", 409), Code(await service.CreateUploadIntentAsync(Admin, "k", Request(Png), default)));
    }

    private static (string?, int?) Code<T>(Fire3D.Application.Authentication.AuthResult<T> result) => (result.Error?.Code, result.Error?.Status);

    private static (LearnMediaService, Store, Storage, Clock) Create(byte[] bytes, string storedType = "image/png")
    {
        var store = new Store(); var storage = new Storage(bytes, storedType); var time = new Clock();
        return (new LearnMediaService(store, storage, time), store, storage, time);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Store : ILearnMediaStore
    {
        public readonly Dictionary<Guid, LearnImageRecord> Rows = [];
        private readonly Dictionary<(Guid, string), Guid> keys = [];
        public int Completions;
        public Task<LearnImageRecord> CreateOrGetIntentAsync(LearnImageRecord candidate, string idempotencyKey, CancellationToken ct)
        {
            if (keys.TryGetValue((candidate.CreatedBy, idempotencyKey), out var id)) return Task.FromResult(Rows[id]);
            keys[(candidate.CreatedBy, idempotencyKey)] = candidate.Id; Rows[candidate.Id] = candidate;
            return Task.FromResult(candidate);
        }
        public Task<LearnImageRecord?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Rows.GetValueOrDefault(id));
        public Task<bool> CompleteAsync(Guid id, string objectKey, int width, int height, CancellationToken ct)
        {
            if (Rows[id].CompletedAt is not null) return Task.FromResult(false);
            Rows[id] = Rows[id] with { ObjectKey = objectKey, Width = width, Height = height, CompletedAt = DateTime.UtcNow };
            Completions++;
            return Task.FromResult(true);
        }
    }

    private sealed class Storage(byte[] bytes, string storedType) : IStorageService
    {
        public bool Down, Missing;
        public string? Copied;
        public readonly List<string> Deleted = [];
        private void Check() { if (Down) throw new HttpRequestException("S3 unavailable"); }
        public Task<string> GeneratePresignedUploadUrlAsync(string objectKey, string mimeType, TimeSpan expiration, CancellationToken ct) { Check(); return Task.FromResult("https://s3.test/put/" + objectKey); }
        public Task<StorageObjectMetadata?> GetObjectMetadataAsync(string objectKey, CancellationToken ct) { Check(); return Task.FromResult(Missing ? null : new StorageObjectMetadata(bytes.Length, storedType, "etag-1")); }
        public Task<byte[]?> ReadObjectAsync(string objectKey, long maxBytes, string expectedETag, CancellationToken ct) { Check(); return Task.FromResult<byte[]?>(bytes); }
        public Task<bool> CopyObjectIfUnchangedAsync(string sourceKey, string sourceETag, string destinationKey, string contentType, CancellationToken ct) { Check(); Copied = destinationKey; return Task.FromResult(true); }
        public Task DeleteObjectAsync(string objectKey, CancellationToken ct) { Check(); Deleted.Add(objectKey); return Task.CompletedTask; }
        public Task<string> GeneratePresignedDownloadUrlAsync(string objectKey, TimeSpan expiration, CancellationToken ct) { Check(); return Task.FromResult("https://s3.test/get/" + objectKey); }
        public Task UploadObjectAsync(string objectKey, Stream content, long contentLength, string contentType, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> VerifyObjectExistsAsync(string objectKey, long expectedSizeBytes, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]?> ReadObjectPrefixAsync(string objectKey, int length, string expectedETag, CancellationToken ct) => throw new NotSupportedException();
    }
}
