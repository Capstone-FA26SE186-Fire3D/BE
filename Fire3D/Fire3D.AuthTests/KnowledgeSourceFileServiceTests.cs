using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fire3D.Application.Ai;
using Fire3D.Application.Authentication;
using Fire3D.Application.Storage;
using Xunit;

namespace Fire3D.AuthTests;

// BE#179: register a knowledge source by uploading the document; the API computes the hash and serves the file at a stable address.
public sealed class KnowledgeSourceFileServiceTests
{
    private static readonly byte[] Pdf = "%PDF-1.7\n1 0 obj << >> endobj\n%%EOF"u8.ToArray();
    private static readonly byte[] Markdown = Encoding.UTF8.GetBytes("# Hướng dẫn sạc xe máy điện an toàn\n\nKhông sạc qua đêm.");
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid Family = Guid.NewGuid();
    private const string Base = "https://api.example.test/";
    private static readonly CompleteKnowledgeSourceUploadRequest Details = new("Common", "Hướng dẫn sạc xe điện", "2026.1", Jurisdiction: "VN");

    private static KnowledgeSourceUploadIntentRequest Request(byte[] bytes, string type = "application/pdf", string name = "guide.pdf") => new(name, type, bytes.Length);

    [Fact]
    public async Task Intent_validates_input_needs_no_hash_and_replays_its_key()
    {
        var (service, store, _, _, _) = Create(Pdf);
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", (await service.CreateUploadIntentAsync(Admin, null, Request(Pdf), default)).Error?.Code);
        var invalid = await service.CreateUploadIntentAsync(Admin, "k", new("", "application/msword", KnowledgeSourceFileRules.MaxBytes + 1), default);
        Assert.Equal(("VALIDATION_ERROR", 400), Code(invalid));
        Assert.Equal(["fileName", "contentType", "sizeBytes"], invalid.Error!.Errors!.Keys);

        var first = await service.CreateUploadIntentAsync(Admin, "doc-1", Request(Pdf), default);
        Assert.True(first.IsSuccess);
        Assert.Equal("application/pdf", first.Value!.Headers["Content-Type"]);
        Assert.Equal($"https://s3.test/put/knowledge/staging/{first.Value.UploadId:N}", first.Value.UploadUrl);
        Assert.Equal(first.Value.UploadId, (await service.CreateUploadIntentAsync(Admin, "doc-1", Request(Pdf), default)).Value!.UploadId);
        Assert.Equal(("IDEMPOTENCY_KEY_CONFLICT", 409), Code(await service.CreateUploadIntentAsync(Admin, "doc-1", Request(Pdf, name: "other.pdf"), default)));
        Assert.Single(store.Rows);
    }

    [Fact]
    public async Task Complete_hashes_the_stored_bytes_and_registers_the_source_at_the_file_address_once()
    {
        var (service, store, storage, gates, _) = Create(Markdown, "text/markdown");
        var uploadId = (await service.CreateUploadIntentAsync(Admin, "doc-1", Request(Markdown, "text/markdown", "guide.md"), default)).Value!.UploadId;
        Assert.Equal("NOT_FOUND", (await service.SignedFileUrlAsync(uploadId, default)).Error?.Code);

        var done = await service.CompleteUploadAsync(Admin, Family, uploadId, Details, Base, default);
        Assert.True(done.IsSuccess, done.Error?.Message);
        var call = gates.Calls.Single();
        Assert.Equal(($"source-file:{uploadId:N}", Convert.ToHexStringLower(SHA256.HashData(Markdown))), (call.Key, call.Input.SourceHash));
        Assert.Equal($"https://api.example.test/api/knowledge-sources/files/{uploadId}", call.Input.SourceUri);
        Assert.Equal(("Common", "Hướng dẫn sạc xe điện", "2026.1", "VN"), (call.Input.Visibility, call.Input.Title, call.Input.VersionLabel, call.Input.Jurisdiction));
        Assert.Equal($"knowledge/files/{uploadId:N}", storage.Copied);
        Assert.Contains($"knowledge/staging/{uploadId:N}", storage.Deleted);
        var source = done.Value.GetProperty("id").GetGuid();
        Assert.Equal(source, store.Rows[uploadId].SourceId);
        Assert.Equal($"https://s3.test/get/knowledge/files/{uploadId:N}", (await service.SignedFileUrlAsync(uploadId, default)).Value);

        // Completing again (lost response, double click) replays the same source without storing the file twice.
        storage.Copied = null;
        Assert.Equal(source, (await service.CompleteUploadAsync(Admin, Family, uploadId, Details, Base, default)).Value.GetProperty("id").GetGuid());
        Assert.Null(storage.Copied);
        Assert.Equal(1, store.Completions);
        // Other details for an already registered file are a conflict; another admin does not find the upload.
        Assert.Equal(("IDEMPOTENCY_KEY_CONFLICT", 409), Code(await service.CompleteUploadAsync(Admin, Family, uploadId, Details with { VersionLabel = "2026.2" }, Base, default)));
        Assert.Equal(("NOT_FOUND", 404), Code(await service.CompleteUploadAsync(Guid.NewGuid(), Family, uploadId, Details, Base, default)));
    }

    [Fact]
    public async Task A_failed_registration_keeps_the_stored_file_and_can_be_retried_with_other_details()
    {
        var (service, store, storage, gates, _) = Create(Pdf);
        var uploadId = (await service.CreateUploadIntentAsync(Admin, "doc-1", Request(Pdf), default)).Value!.UploadId;
        gates.Fail = new AuthError("NOT_FOUND", "Organization not found.", 404);
        Assert.Equal(("NOT_FOUND", 404), Code(await service.CompleteUploadAsync(Admin, Family, uploadId, Details, Base, default)));
        Assert.NotNull(store.Rows[uploadId].CompletedAt);
        Assert.Null(store.Rows[uploadId].SourceId);
        Assert.Equal("NOT_FOUND", (await service.SignedFileUrlAsync(uploadId, default)).Error?.Code);

        gates.Fail = null; storage.Copied = null;
        Assert.True((await service.CompleteUploadAsync(Admin, Family, uploadId, Details, Base, default)).IsSuccess);
        Assert.Null(storage.Copied);
        Assert.NotNull(store.Rows[uploadId].SourceId);
    }

    [Fact]
    public async Task Complete_rejects_files_that_are_not_the_declared_kind()
    {
        // Text declared as PDF.
        var (service, _, _, gates, _) = Create(Markdown);
        var id = (await service.CreateUploadIntentAsync(Admin, "k", Request(Markdown), default)).Value!.UploadId;
        Assert.Equal(("KNOWLEDGE_SOURCE_FILE_INVALID", 422), Code(await service.CompleteUploadAsync(Admin, Family, id, Details, Base, default)));
        Assert.Empty(gates.Calls);

        // Bytes that are not UTF-8 text, and text with NUL bytes.
        foreach (var bytes in new[] { new byte[] { 0x48, 0xC3, 0x28 }, "a\0b"u8.ToArray() })
        {
            (service, _, _, _, _) = Create(bytes, "text/plain");
            id = (await service.CreateUploadIntentAsync(Admin, "k", Request(bytes, "text/plain", "a.txt"), default)).Value!.UploadId;
            Assert.Equal(("KNOWLEDGE_SOURCE_FILE_INVALID", 422), Code(await service.CompleteUploadAsync(Admin, Family, id, Details, Base, default)));
        }

        // The object S3 holds differs in type from the intent.
        (service, _, _, _, _) = Create(Pdf, "text/html");
        id = (await service.CreateUploadIntentAsync(Admin, "k", Request(Pdf), default)).Value!.UploadId;
        Assert.Equal(("VALIDATION_ERROR", 400), Code(await service.CompleteUploadAsync(Admin, Family, id, Details, Base, default)));
    }

    [Fact]
    public async Task Complete_reports_missing_changed_expired_and_unavailable_uploads()
    {
        var (service, _, storage, gates, time) = Create(Pdf);
        var id = (await service.CreateUploadIntentAsync(Admin, "k", Request(Pdf), default)).Value!.UploadId;
        storage.Missing = true;
        Assert.Equal(("KNOWLEDGE_SOURCE_UPLOAD_MISSING", 409), Code(await service.CompleteUploadAsync(Admin, Family, id, Details, Base, default)));
        storage.Missing = false; storage.Unchanged = false;
        Assert.Equal(("KNOWLEDGE_SOURCE_UPLOAD_CHANGED", 409), Code(await service.CompleteUploadAsync(Admin, Family, id, Details, Base, default)));
        storage.Unchanged = true; storage.Down = true;
        Assert.Equal(("KNOWLEDGE_SOURCE_STORAGE_UNAVAILABLE", 503), Code(await service.CompleteUploadAsync(Admin, Family, id, Details, Base, default)));
        Assert.Equal(("KNOWLEDGE_SOURCE_STORAGE_UNAVAILABLE", 503), Code(await service.CreateUploadIntentAsync(Admin, "k2", Request(Pdf), default)));
        storage.Down = false;
        time.Now += KnowledgeSourceFileRules.UploadLifetime;
        Assert.Equal(("KNOWLEDGE_SOURCE_UPLOAD_EXPIRED", 409), Code(await service.CompleteUploadAsync(Admin, Family, id, Details, Base, default)));
        Assert.Equal(("KNOWLEDGE_SOURCE_UPLOAD_EXPIRED", 409), Code(await service.CreateUploadIntentAsync(Admin, "k", Request(Pdf), default)));
        Assert.Empty(gates.Calls);
    }

    private static (string?, int?) Code<T>(AuthResult<T> result) => (result.Error?.Code, result.Error?.Status);

    private static (KnowledgeSourceFileService, Store, Storage, Gates, Clock) Create(byte[] bytes, string storedType = "application/pdf")
    {
        var store = new Store(); var storage = new Storage(bytes, storedType); var gates = new Gates(); var time = new Clock();
        return (new KnowledgeSourceFileService(store, storage, gates, time), store, storage, gates, time);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 27, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // CreateSource as the admin gate answers it: one source per key, the same key with other input is a conflict.
    private sealed class Gates : IOrganizationAiGates
    {
        public readonly List<(string Key, CreateKnowledgeSourceRequest Input)> Calls = [];
        private readonly Dictionary<string, (CreateKnowledgeSourceRequest Input, Guid Id)> receipts = [];
        public AuthError? Fail;
        public Task<AuthResult<JsonElement>> AdminAsync(string action, Guid actor, Guid family, Guid resource, object input, string? key, long? expected, CancellationToken ct)
        {
            Assert.Equal(("CreateSource", Admin, Family), (action, actor, family));
            var request = (CreateKnowledgeSourceRequest)input;
            Calls.Add((key!, request));
            if (Fail is not null) return Task.FromResult(AuthResult<JsonElement>.Fail(Fail.Code, Fail.Message, Fail.Status));
            if (receipts.TryGetValue(key!, out var receipt) && receipt.Input != request)
                return Task.FromResult(AuthResult<JsonElement>.Fail("IDEMPOTENCY_KEY_CONFLICT", "Conflict.", 409));
            var id = receipts.TryGetValue(key!, out receipt) ? receipt.Id : Guid.NewGuid();
            receipts[key!] = (request, id);
            return Task.FromResult(AuthResult<JsonElement>.Ok(JsonSerializer.SerializeToElement(new { id, sourceUri = request.SourceUri, sourceHash = request.SourceHash, approvalStatus = "Draft" })));
        }
        public Task<AuthResult<JsonElement>> OrganizationAsync(string action, Guid actor, Guid family, Guid resource, object input, string? key, CancellationToken ct) => throw new NotSupportedException();
        public Task<AuthResult<JsonElement>> OrganizationRequestsAsync(Guid actor, Guid family, object input, CancellationToken ct) => throw new NotSupportedException();
        public Task<AuthResult<JsonElement>> TraineeAsync(string action, Guid actor, Guid family, Guid resource, object input, string? key, CancellationToken ct) => throw new NotSupportedException();
        public Task<JsonElement> WorkerAsync(string action, string worker, Guid? request, Guid? lease, object input, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Store : IKnowledgeSourceFileStore
    {
        public readonly Dictionary<Guid, KnowledgeSourceFileRecord> Rows = [];
        private readonly Dictionary<(Guid, string), Guid> keys = [];
        public int Completions;
        public Task<KnowledgeSourceFileRecord> CreateOrGetIntentAsync(KnowledgeSourceFileRecord candidate, string idempotencyKey, CancellationToken ct)
        {
            if (keys.TryGetValue((candidate.CreatedBy, idempotencyKey), out var id)) return Task.FromResult(Rows[id]);
            keys[(candidate.CreatedBy, idempotencyKey)] = candidate.Id; Rows[candidate.Id] = candidate;
            return Task.FromResult(candidate);
        }
        public Task<KnowledgeSourceFileRecord?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(Rows.GetValueOrDefault(id));
        public Task<bool> CompleteAsync(Guid id, string objectKey, string sha256, CancellationToken ct)
        {
            if (Rows[id].CompletedAt is not null) return Task.FromResult(false);
            Rows[id] = Rows[id] with { ObjectKey = objectKey, Sha256 = sha256, CompletedAt = DateTime.UtcNow };
            Completions++;
            return Task.FromResult(true);
        }
        public Task LinkSourceAsync(Guid id, Guid sourceId, CancellationToken ct)
        {
            Rows[id] = Rows[id] with { SourceId = Rows[id].SourceId ?? sourceId };
            return Task.CompletedTask;
        }
    }

    // Also stands in for S3 in the Postgres test of the same flow.
    internal sealed class Storage(byte[] bytes, string storedType) : IStorageService
    {
        public bool Down, Missing, Unchanged = true;
        public string? Copied;
        public readonly List<string> Deleted = [];
        private void Check() { if (Down) throw new HttpRequestException("S3 unavailable"); }
        public Task<string> GeneratePresignedUploadUrlAsync(string objectKey, string mimeType, TimeSpan expiration, CancellationToken ct) { Check(); return Task.FromResult("https://s3.test/put/" + objectKey); }
        public Task<StorageObjectMetadata?> GetObjectMetadataAsync(string objectKey, CancellationToken ct) { Check(); return Task.FromResult(Missing ? null : new StorageObjectMetadata(bytes.Length, storedType, "etag-1")); }
        public Task<byte[]?> ReadObjectAsync(string objectKey, long maxBytes, string expectedETag, CancellationToken ct) { Check(); return Task.FromResult(Unchanged ? bytes : null); }
        public Task<bool> CopyObjectIfUnchangedAsync(string sourceKey, string sourceETag, string destinationKey, string contentType, CancellationToken ct) { Check(); Copied = destinationKey; return Task.FromResult(true); }
        public Task DeleteObjectAsync(string objectKey, CancellationToken ct) { Check(); Deleted.Add(objectKey); return Task.CompletedTask; }
        public Task<string> GeneratePresignedDownloadUrlAsync(string objectKey, TimeSpan expiration, CancellationToken ct) { Check(); return Task.FromResult("https://s3.test/get/" + objectKey); }
        public Task UploadObjectAsync(string objectKey, Stream content, long contentLength, string contentType, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> VerifyObjectExistsAsync(string objectKey, long expectedSizeBytes, CancellationToken ct) => throw new NotSupportedException();
        public Task<byte[]?> ReadObjectPrefixAsync(string objectKey, int length, string expectedETag, CancellationToken ct) => throw new NotSupportedException();
    }
}
