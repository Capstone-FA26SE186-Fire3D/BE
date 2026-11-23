using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Storage;

namespace Fire3D.Application.Buildings;

/// <summary>The signed PC06 plan of a Building (BE#212): application/pdf only, at most 20 MB.</summary>
public sealed record BuildingPlanUploadIntentRequest(string FileName, string ContentType, long SizeBytes);
/// <summary>PUT the PDF to uploadUrl with exactly these headers before expiresAt, then call complete with uploadId.</summary>
public sealed record BuildingPlanUploadIntentResponse(Guid UploadId, string UploadUrl, IReadOnlyDictionary<string, string> Headers, DateTime ExpiresAt);
/// <summary>The stored plan; Sha256 is computed by the API from the stored bytes.</summary>
public sealed record BuildingPlanDocumentResponse(Guid Id, Guid BuildingId, string FileName, long SizeBytes, string Sha256, DateTime StoredAt);

/// <summary>An upload: an intent until CompletedAt is set, then an immutable private object at ObjectKey with its Sha256.</summary>
public sealed record BuildingPlanDocumentRecord(Guid Id, Guid BuildingId, Guid CreatedBy, string InputHash, string FileName, string ContentType, long SizeBytes,
    string StagingKey, string? ObjectKey, string? Sha256, DateTime CreatedAt, DateTime ExpiresAt, DateTime? CompletedAt);

public interface IBuildingPlanDocumentStore
{
    /// <summary>Organization of an active, not deleted Building; null otherwise.</summary>
    Task<Guid?> BuildingOrganizationAsync(Guid buildingId, CancellationToken ct);
    Task<BuildingPlanDocumentRecord> CreateOrGetIntentAsync(BuildingPlanDocumentRecord candidate, string idempotencyKey, CancellationToken ct);
    Task<BuildingPlanDocumentRecord?> FindAsync(Guid id, CancellationToken ct);
    Task<bool> CompleteAsync(Guid id, string objectKey, string sha256, CancellationToken ct);
    /// <summary>Sets the Building's signed plan and moves its ETag (updated_at).</summary>
    Task AttachAsync(Guid buildingId, Guid documentId, DateTime now, CancellationToken ct);
    /// <summary>The stored document the Building currently points at, if any.</summary>
    Task<BuildingPlanDocumentRecord?> CurrentAsync(Guid buildingId, CancellationToken ct);
}

public interface IBuildingPlanDocuments
{
    Task<AuthResult<BuildingPlanUploadIntentResponse>> CreateUploadIntentAsync(Guid actor, Guid buildingId, string? idempotencyKey, BuildingPlanUploadIntentRequest request, CancellationToken ct);
    Task<AuthResult<BuildingPlanDocumentResponse>> CompleteUploadAsync(Guid actor, Guid buildingId, Guid uploadId, CancellationToken ct);
    /// <summary>A 10-minute signed URL of the Building's current signed plan; 404 when there is none.</summary>
    Task<AuthResult<string>> SignedUrlAsync(Guid actor, Guid buildingId, CancellationToken ct);
}

public static class BuildingPlanDocumentRules
{
    public const long MaxBytes = 20 * 1024 * 1024;
    public const string ContentType = "application/pdf";
    public static readonly TimeSpan UploadLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ReadUrlLifetime = TimeSpan.FromMinutes(10);
    public static string StagingKey(Guid building, Guid id) => $"buildings/{building:N}/signed-plans/staging/{id:N}";
    public static string FileKey(Guid building, Guid id) => $"buildings/{building:N}/signed-plans/{id:N}.pdf";

    public static Dictionary<string, string[]> Validate(BuildingPlanUploadIntentRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null) { errors["body"] = ["Send fileName, contentType and sizeBytes."]; return errors; }
        var name = request.FileName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name.Any(char.IsControl)) errors["fileName"] = ["Use 1-255 characters without control characters."];
        if (!string.Equals(request.ContentType?.Trim(), ContentType, StringComparison.OrdinalIgnoreCase)) errors["contentType"] = ["The signed plan must be application/pdf."];
        if (request.SizeBytes is < 1 or > MaxBytes) errors["sizeBytes"] = [$"Use 1-{MaxBytes} bytes (20 MB)."];
        return errors;
    }

    public static string InputHash(BuildingPlanUploadIntentRequest request) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { fileName = request.FileName.Trim(), sizeBytes = request.SizeBytes }))));
}

/// <summary>
/// OrganizationUser of the Building's organization (or PlatformAdmin) uploads the signed PC06 plan through a presigned PUT to a private
/// staging object; complete checks size, type and the %PDF- signature, computes the SHA-256, copies it to an immutable private object and
/// makes it the Building's signed plan. Reading returns a short signed URL; nothing is public.
/// </summary>
public sealed class BuildingPlanDocumentService(IBuildingPlanDocumentStore store, IStorageService storage, IAuthStore accounts, TimeProvider clock) : IBuildingPlanDocuments
{
    public async Task<AuthResult<BuildingPlanUploadIntentResponse>> CreateUploadIntentAsync(Guid actor, Guid buildingId, string? idempotencyKey, BuildingPlanUploadIntentRequest request, CancellationToken ct)
    {
        if (!BuildingCommandKey.IsValid(idempotencyKey))
            return AuthResult<BuildingPlanUploadIntentResponse>.Fail("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key must contain 1-128 printable non-whitespace characters.", 400);
        var errors = BuildingPlanDocumentRules.Validate(request);
        if (errors.Count > 0) return AuthResult<BuildingPlanUploadIntentResponse>.Fail("VALIDATION_ERROR", "The signed plan does not meet the upload rules.", 400, errors);
        if (await AuthorizeAsync(actor, buildingId, ct) is { } denied) return new(default, denied);
        var now = clock.GetUtcNow().UtcDateTime;
        var id = Guid.NewGuid();
        var candidate = new BuildingPlanDocumentRecord(id, buildingId, actor, BuildingPlanDocumentRules.InputHash(request), request.FileName.Trim(),
            BuildingPlanDocumentRules.ContentType, request.SizeBytes, BuildingPlanDocumentRules.StagingKey(buildingId, id), null, null, now,
            now.Add(BuildingPlanDocumentRules.UploadLifetime), null);
        var stored = await store.CreateOrGetIntentAsync(candidate, idempotencyKey!, ct);
        if (stored.InputHash != candidate.InputHash || stored.BuildingId != buildingId)
            return AuthResult<BuildingPlanUploadIntentResponse>.Fail("IDEMPOTENCY_KEY_CONFLICT", "This Idempotency-Key was used for another file.", 409);
        if (stored.CompletedAt is null && stored.ExpiresAt <= now) return Expired<BuildingPlanUploadIntentResponse>();
        var remaining = stored.CompletedAt is null ? stored.ExpiresAt - now : BuildingPlanDocumentRules.UploadLifetime;
        var (reachable, url) = await Storage(() => storage.GeneratePresignedUploadUrlAsync(stored.StagingKey, stored.ContentType, remaining, ct));
        return reachable ? AuthResult<BuildingPlanUploadIntentResponse>.Ok(new(stored.Id, url, new Dictionary<string, string> { ["Content-Type"] = stored.ContentType }, stored.ExpiresAt))
            : Unavailable<BuildingPlanUploadIntentResponse>();
    }

    public async Task<AuthResult<BuildingPlanDocumentResponse>> CompleteUploadAsync(Guid actor, Guid buildingId, Guid uploadId, CancellationToken ct)
    {
        if (await AuthorizeAsync(actor, buildingId, ct) is { } denied) return new(default, denied);
        var record = await store.FindAsync(uploadId, ct);
        // An upload belongs to whoever created it, for that Building only.
        if (record is null || record.CreatedBy != actor || record.BuildingId != buildingId) return AuthResult<BuildingPlanDocumentResponse>.Fail("NOT_FOUND", "Upload not found.", 404);
        if (record.CompletedAt is null)
        {
            var stored = await StoreAsync(record, ct);
            if (!stored.IsSuccess) return AuthResult<BuildingPlanDocumentResponse>.Fail(stored.Error!.Code, stored.Error.Message, stored.Error.Status);
            record = stored.Value!;
            await store.AttachAsync(buildingId, record.Id, clock.GetUtcNow().UtcDateTime, ct);
        }
        return AuthResult<BuildingPlanDocumentResponse>.Ok(new(record.Id, record.BuildingId, record.FileName, record.SizeBytes, record.Sha256!, record.CompletedAt!.Value));
    }

    public async Task<AuthResult<string>> SignedUrlAsync(Guid actor, Guid buildingId, CancellationToken ct)
    {
        if (await AuthorizeAsync(actor, buildingId, ct) is { } denied) return new(default, denied);
        var current = await store.CurrentAsync(buildingId, ct);
        if (current?.ObjectKey is null) return AuthResult<string>.Fail("NOT_FOUND", "This Building has no signed plan.", 404);
        var (reachable, url) = await Storage(() => storage.GeneratePresignedDownloadUrlAsync(current.ObjectKey, BuildingPlanDocumentRules.ReadUrlLifetime, ct));
        return reachable ? AuthResult<string>.Ok(url) : Unavailable<string>();
    }

    // Same rule as Building edits: the Building's OrganizationUser or PlatformAdmin, Building active.
    private async Task<AuthError?> AuthorizeAsync(Guid actor, Guid buildingId, CancellationToken ct)
    {
        var organization = await store.BuildingOrganizationAsync(buildingId, ct);
        if (organization is null) return new AuthError("NOT_FOUND", "Building not found.", 404);
        return await BuildingAuthorization.CanMutateAsync(accounts, actor, organization.Value, ct) ? null : new AuthError("FORBIDDEN", "An active organization scope is required.", 403);
    }

    private async Task<AuthResult<BuildingPlanDocumentRecord>> StoreAsync(BuildingPlanDocumentRecord record, CancellationToken ct)
    {
        if (record.ExpiresAt <= clock.GetUtcNow().UtcDateTime) return Expired<BuildingPlanDocumentRecord>();
        var (reachable, metadata) = await Storage(() => storage.GetObjectMetadataAsync(record.StagingKey, ct));
        if (!reachable) return Unavailable<BuildingPlanDocumentRecord>();
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.ETag))
            return AuthResult<BuildingPlanDocumentRecord>.Fail("SIGNED_PLAN_UPLOAD_MISSING", "PUT the file to uploadUrl before completing.", 409);
        if (metadata.ContentLength != record.SizeBytes || !string.Equals(metadata.ContentType, record.ContentType, StringComparison.OrdinalIgnoreCase))
            return AuthResult<BuildingPlanDocumentRecord>.Fail("VALIDATION_ERROR", "The uploaded file does not match the declared size or content type.", 400);
        (reachable, var bytes) = await Storage(() => storage.ReadObjectAsync(record.StagingKey, BuildingPlanDocumentRules.MaxBytes, metadata.ETag, ct));
        if (!reachable) return Unavailable<BuildingPlanDocumentRecord>();
        if (bytes is null) return Changed();
        if (!bytes.AsSpan().StartsWith("%PDF-"u8))
            return AuthResult<BuildingPlanDocumentRecord>.Fail("SIGNED_PLAN_NOT_PDF", "The signed plan is not a PDF document.", 422);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var objectKey = BuildingPlanDocumentRules.FileKey(record.BuildingId, record.Id);
        (reachable, var copied) = await Storage(() => storage.CopyObjectIfUnchangedAsync(record.StagingKey, metadata.ETag, objectKey, record.ContentType, ct));
        if (!reachable) return Unavailable<BuildingPlanDocumentRecord>();
        if (!copied) return Changed();
        if (!await store.CompleteAsync(record.Id, objectKey, sha256, ct))
        {
            var winner = await store.FindAsync(record.Id, ct);
            return winner?.CompletedAt is not null ? AuthResult<BuildingPlanDocumentRecord>.Ok(winner) : Expired<BuildingPlanDocumentRecord>();
        }
        await Storage(async () => { await storage.DeleteObjectAsync(record.StagingKey, ct); return true; });
        return AuthResult<BuildingPlanDocumentRecord>.Ok(record with { ObjectKey = objectKey, Sha256 = sha256, CompletedAt = clock.GetUtcNow().UtcDateTime });
    }

    private static async Task<(bool Reachable, T Value)> Storage<T>(Func<Task<T>> call)
    {
        try { return (true, await call()); }
        catch (Exception e) when (e is not OperationCanceledException) { return (false, default!); }
    }
    private static AuthResult<BuildingPlanDocumentRecord> Changed() =>
        AuthResult<BuildingPlanDocumentRecord>.Fail("SIGNED_PLAN_UPLOAD_CHANGED", "The upload changed while it was checked. Upload again.", 409);
    private static AuthResult<T> Expired<T>() => AuthResult<T>.Fail("SIGNED_PLAN_UPLOAD_EXPIRED", "This upload expired. Start again with a new Idempotency-Key.", 409);
    private static AuthResult<T> Unavailable<T>() => AuthResult<T>.Fail("SIGNED_PLAN_STORAGE_UNAVAILABLE", "File storage is unavailable. Try again later.", 503);
}
