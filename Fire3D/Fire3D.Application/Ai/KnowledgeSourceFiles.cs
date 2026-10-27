using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Buildings;
using Fire3D.Application.Storage;

namespace Fire3D.Application.Ai;

/// <summary>PDF, Markdown, plain text or HTML, at most 20 MB. No hash: the API computes the SHA-256 of what is stored.</summary>
public sealed record KnowledgeSourceUploadIntentRequest(string FileName, string ContentType, long SizeBytes);
/// <summary>PUT the file to uploadUrl with exactly these headers before expiresAt, then call complete with uploadId.</summary>
public sealed record KnowledgeSourceUploadIntentResponse(Guid UploadId, string UploadUrl, IReadOnlyDictionary<string, string> Headers, DateTime ExpiresAt);
/// <summary>The source version the uploaded file becomes; same rules as registering by URL, without sourceUri and sourceHash.</summary>
public sealed record CompleteKnowledgeSourceUploadRequest(string Visibility, string Title, string VersionLabel, Guid? OrganizationId = null,
    string? Jurisdiction = null, DateTimeOffset? EffectiveFrom = null, DateTimeOffset? EffectiveUntil = null);

/// <summary>An upload: an intent until CompletedAt is set, then an immutable file at ObjectKey with its Sha256, linked to the source it registered.</summary>
public sealed record KnowledgeSourceFileRecord(Guid Id, Guid CreatedBy, string InputHash, string FileName, string ContentType, long SizeBytes,
    string StagingKey, string? ObjectKey, string? Sha256, Guid? SourceId, DateTime CreatedAt, DateTime ExpiresAt, DateTime? CompletedAt);

public interface IKnowledgeSourceFileStore
{
    /// <summary>Inserts the intent unless the actor already used the key; returns the row stored under (actor, key).</summary>
    Task<KnowledgeSourceFileRecord> CreateOrGetIntentAsync(KnowledgeSourceFileRecord candidate, string idempotencyKey, CancellationToken ct);
    Task<KnowledgeSourceFileRecord?> FindAsync(Guid id, CancellationToken ct);
    /// <summary>Marks an open intent stored with its final object and hash; false if it was already completed.</summary>
    Task<bool> CompleteAsync(Guid id, string objectKey, string sha256, CancellationToken ct);
    /// <summary>Records the source the file registered; a file already linked keeps its link.</summary>
    Task LinkSourceAsync(Guid id, Guid sourceId, CancellationToken ct);
}

public interface IKnowledgeSourceFileService
{
    Task<AuthResult<KnowledgeSourceUploadIntentResponse>> CreateUploadIntentAsync(Guid actor, string? idempotencyKey, KnowledgeSourceUploadIntentRequest request, CancellationToken ct);
    /// <summary>Stores the uploaded file and registers it as a Draft knowledge source; returns the source as POST /api/admin/knowledge-sources does.</summary>
    Task<AuthResult<JsonElement>> CompleteUploadAsync(Guid actor, Guid family, Guid uploadId, CompleteKnowledgeSourceUploadRequest request, string publicBaseUrl, CancellationToken ct);
    /// <summary>A short-lived signed URL of a file that registered a source; 404 NOT_FOUND otherwise, 503 when storage is down.</summary>
    Task<AuthResult<string>> SignedFileUrlAsync(Guid fileId, CancellationToken ct);
}

public static class KnowledgeSourceFileRules
{
    // The AI service downloads sources up to 20 MB (rag-api app/sources/fetch.py).
    public const long MaxBytes = 20 * 1024 * 1024;
    public static readonly string[] ContentTypes = ["application/pdf", "text/markdown", "text/plain", "text/html"];
    public static readonly TimeSpan UploadLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ReadUrlLifetime = TimeSpan.FromMinutes(10);
    public static string StagingKey(Guid id) => $"knowledge/staging/{id:N}";
    public static string FileKey(Guid id) => $"knowledge/files/{id:N}";
    public static string PublicPath(Guid id) => $"/api/knowledge-sources/files/{id}";
    // The registration replays under one key per file, so completing again returns the same source.
    public static string RegistrationKey(Guid id) => $"source-file:{id:N}";

    public static Dictionary<string, string[]> Validate(KnowledgeSourceUploadIntentRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null) { errors["body"] = ["Send fileName, contentType and sizeBytes."]; return errors; }
        var name = request.FileName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name.Any(char.IsControl)) errors["fileName"] = ["Use 1-255 characters without control characters."];
        if (request.ContentType is null || !ContentTypes.Contains(request.ContentType.Trim().ToLowerInvariant())) errors["contentType"] = ["Use application/pdf, text/markdown, text/plain or text/html."];
        if (request.SizeBytes is < 1 or > MaxBytes) errors["sizeBytes"] = [$"Use 1-{MaxBytes} bytes (20 MB)."];
        return errors;
    }

    /// <summary>Hash of the normalized request, so the same Idempotency-Key with other input is a conflict.</summary>
    public static string InputHash(KnowledgeSourceUploadIntentRequest request) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        fileName = request.FileName.Trim(), contentType = request.ContentType.Trim().ToLowerInvariant(), sizeBytes = request.SizeBytes
    }))));

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>A PDF starts with %PDF-; the text types must be UTF-8 without NUL bytes.</summary>
    public static bool ContentMatches(byte[] bytes, string contentType)
    {
        if (contentType == "application/pdf") return bytes.AsSpan().StartsWith("%PDF-"u8);
        if (Array.IndexOf(bytes, (byte)0) >= 0) return false;
        try { StrictUtf8.GetCharCount(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }
}

/// <summary>
/// PlatformAdmin registers a knowledge source by uploading its document (BE#179): an intent returns a presigned PUT to a private
/// staging object; complete reads the stored bytes, checks size, type and content, computes their SHA-256, copies them to an
/// immutable object and registers the source through the admin gate with the file's stable public address as sourceUri.
/// </summary>
public sealed class KnowledgeSourceFileService(IKnowledgeSourceFileStore store, IStorageService storage, IOrganizationAiGates gates, TimeProvider clock) : IKnowledgeSourceFileService
{
    public async Task<AuthResult<KnowledgeSourceUploadIntentResponse>> CreateUploadIntentAsync(Guid actor, string? idempotencyKey, KnowledgeSourceUploadIntentRequest request, CancellationToken ct)
    {
        if (!BuildingCommandKey.IsValid(idempotencyKey))
            return AuthResult<KnowledgeSourceUploadIntentResponse>.Fail("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key must contain 1-128 printable non-whitespace characters.", 400);
        var errors = KnowledgeSourceFileRules.Validate(request);
        if (errors.Count > 0) return AuthResult<KnowledgeSourceUploadIntentResponse>.Fail("VALIDATION_ERROR", "The file does not meet the upload rules.", 400, errors);
        var now = clock.GetUtcNow().UtcDateTime;
        var id = Guid.NewGuid();
        var candidate = new KnowledgeSourceFileRecord(id, actor, KnowledgeSourceFileRules.InputHash(request), request.FileName.Trim(), request.ContentType.Trim().ToLowerInvariant(),
            request.SizeBytes, KnowledgeSourceFileRules.StagingKey(id), null, null, null, now, now.Add(KnowledgeSourceFileRules.UploadLifetime), null);
        var stored = await store.CreateOrGetIntentAsync(candidate, idempotencyKey!, ct);
        if (stored.InputHash != candidate.InputHash)
            return AuthResult<KnowledgeSourceUploadIntentResponse>.Fail("IDEMPOTENCY_KEY_CONFLICT", "This Idempotency-Key was used for another file.", 409);
        if (stored.CompletedAt is null && stored.ExpiresAt <= now) return Expired<KnowledgeSourceUploadIntentResponse>();
        var remaining = stored.CompletedAt is null ? stored.ExpiresAt - now : KnowledgeSourceFileRules.UploadLifetime;
        var (reachable, url) = await Storage(() => storage.GeneratePresignedUploadUrlAsync(stored.StagingKey, stored.ContentType, remaining, ct));
        return reachable ? AuthResult<KnowledgeSourceUploadIntentResponse>.Ok(new(stored.Id, url, new Dictionary<string, string> { ["Content-Type"] = stored.ContentType }, stored.ExpiresAt))
            : Unavailable<KnowledgeSourceUploadIntentResponse>();
    }

    public async Task<AuthResult<JsonElement>> CompleteUploadAsync(Guid actor, Guid family, Guid uploadId, CompleteKnowledgeSourceUploadRequest request, string publicBaseUrl, CancellationToken ct)
    {
        var record = await store.FindAsync(uploadId, ct);
        // Another admin's upload is not found either: an upload belongs to whoever created it.
        if (record is null || record.CreatedBy != actor) return AuthResult<JsonElement>.Fail("NOT_FOUND", "Upload not found.", 404);
        if (record.CompletedAt is null)
        {
            var stored = await StoreAsync(record, ct);
            if (!stored.IsSuccess) return AuthResult<JsonElement>.Fail(stored.Error!.Code, stored.Error.Message, stored.Error.Status);
            record = stored.Value!;
        }
        var input = new CreateKnowledgeSourceRequest(request.Visibility, request.Title, request.VersionLabel, record.Sha256!, request.OrganizationId,
            publicBaseUrl.TrimEnd('/') + KnowledgeSourceFileRules.PublicPath(record.Id), request.Jurisdiction, request.EffectiveFrom, request.EffectiveUntil);
        var registered = await gates.AdminAsync("CreateSource", actor, family, Guid.Empty, input, KnowledgeSourceFileRules.RegistrationKey(record.Id), null, ct);
        if (registered.IsSuccess && record.SourceId is null) await store.LinkSourceAsync(record.Id, registered.Value.GetProperty("id").GetGuid(), ct);
        return registered;
    }

    public async Task<AuthResult<string>> SignedFileUrlAsync(Guid fileId, CancellationToken ct)
    {
        var record = await store.FindAsync(fileId, ct);
        if (record?.SourceId is null || record.ObjectKey is null) return AuthResult<string>.Fail("NOT_FOUND", "File not found.", 404);
        var (reachable, url) = await Storage(() => storage.GeneratePresignedDownloadUrlAsync(record.ObjectKey, KnowledgeSourceFileRules.ReadUrlLifetime, ct));
        return reachable ? AuthResult<string>.Ok(url) : Unavailable<string>();
    }

    // Checks the staged bytes and copies them to the immutable object; the hash is computed here, never taken from the client.
    private async Task<AuthResult<KnowledgeSourceFileRecord>> StoreAsync(KnowledgeSourceFileRecord record, CancellationToken ct)
    {
        if (record.ExpiresAt <= clock.GetUtcNow().UtcDateTime) return Expired<KnowledgeSourceFileRecord>();
        var (reachable, metadata) = await Storage(() => storage.GetObjectMetadataAsync(record.StagingKey, ct));
        if (!reachable) return Unavailable<KnowledgeSourceFileRecord>();
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.ETag))
            return AuthResult<KnowledgeSourceFileRecord>.Fail("KNOWLEDGE_SOURCE_UPLOAD_MISSING", "PUT the file to uploadUrl before completing.", 409);
        if (metadata.ContentLength != record.SizeBytes || !string.Equals(metadata.ContentType, record.ContentType, StringComparison.OrdinalIgnoreCase))
            return AuthResult<KnowledgeSourceFileRecord>.Fail("VALIDATION_ERROR", "The uploaded file does not match the declared size or content type.", 400);
        (reachable, var bytes) = await Storage(() => storage.ReadObjectAsync(record.StagingKey, KnowledgeSourceFileRules.MaxBytes, metadata.ETag, ct));
        if (!reachable) return Unavailable<KnowledgeSourceFileRecord>();
        if (bytes is null) return Changed();
        if (!KnowledgeSourceFileRules.ContentMatches(bytes, record.ContentType))
            return AuthResult<KnowledgeSourceFileRecord>.Fail("KNOWLEDGE_SOURCE_FILE_INVALID", record.ContentType == "application/pdf"
                ? "The file is not a PDF document." : "The file must be UTF-8 text.", 422);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var objectKey = KnowledgeSourceFileRules.FileKey(record.Id);
        (reachable, var copied) = await Storage(() => storage.CopyObjectIfUnchangedAsync(record.StagingKey, metadata.ETag, objectKey, record.ContentType, ct));
        if (!reachable) return Unavailable<KnowledgeSourceFileRecord>();
        if (!copied) return Changed();
        if (!await store.CompleteAsync(record.Id, objectKey, sha256, ct))
        {
            // A concurrent complete stored the same intent first; continue with what it stored.
            var winner = await store.FindAsync(record.Id, ct);
            return winner?.CompletedAt is not null ? AuthResult<KnowledgeSourceFileRecord>.Ok(winner) : Expired<KnowledgeSourceFileRecord>();
        }
        // A leftover staging object is harmless: the file is already final.
        await Storage(async () => { await storage.DeleteObjectAsync(record.StagingKey, ct); return true; });
        return AuthResult<KnowledgeSourceFileRecord>.Ok(record with { ObjectKey = objectKey, Sha256 = sha256, CompletedAt = clock.GetUtcNow().UtcDateTime });
    }

    // Only storage failures become 503; database errors keep their own handling.
    private static async Task<(bool Reachable, T Value)> Storage<T>(Func<Task<T>> call)
    {
        try { return (true, await call()); }
        catch (Exception e) when (e is not OperationCanceledException) { return (false, default!); }
    }
    private static AuthResult<KnowledgeSourceFileRecord> Changed() =>
        AuthResult<KnowledgeSourceFileRecord>.Fail("KNOWLEDGE_SOURCE_UPLOAD_CHANGED", "The upload changed while it was checked. Upload again.", 409);
    private static AuthResult<T> Expired<T>() => AuthResult<T>.Fail("KNOWLEDGE_SOURCE_UPLOAD_EXPIRED", "This upload expired. Start again with a new Idempotency-Key.", 409);
    private static AuthResult<T> Unavailable<T>() => AuthResult<T>.Fail("KNOWLEDGE_SOURCE_STORAGE_UNAVAILABLE", "File storage is unavailable. Try again later.", 503);
}
