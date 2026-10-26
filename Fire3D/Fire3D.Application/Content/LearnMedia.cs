using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Buildings;
using Fire3D.Application.Storage;

namespace Fire3D.Application.Content;

/// <summary>sha256 is the lowercase hex SHA-256 of the file; the bytes uploaded to uploadUrl must match it.</summary>
public sealed record LearnImageUploadIntentRequest(string FileName, string ContentType, long SizeBytes, string Sha256);
/// <summary>PUT the file to uploadUrl with exactly these headers before expiresAt, then call complete with uploadId.</summary>
public sealed record LearnImageUploadIntentResponse(Guid UploadId, string UploadUrl, IReadOnlyDictionary<string, string> Headers, DateTime ExpiresAt);
/// <summary>Url is a stable public https address for coverImageUrl or an image block url; it redirects to a short-lived signed URL.</summary>
public sealed record LearnImageResponse(Guid ImageId, string Url, int Width, int Height, string ContentType, long SizeBytes);

/// <summary>A stored upload: an intent until CompletedAt is set, then an immutable image at ObjectKey.</summary>
public sealed record LearnImageRecord(Guid Id, Guid CreatedBy, string InputHash, string FileName, string ContentType, long SizeBytes,
    string Sha256, string StagingKey, string? ObjectKey, int? Width, int? Height, DateTime CreatedAt, DateTime ExpiresAt, DateTime? CompletedAt);

public interface ILearnMediaStore
{
    /// <summary>Inserts the intent unless the actor already used the key; returns the row stored under (actor, key).</summary>
    Task<LearnImageRecord> CreateOrGetIntentAsync(LearnImageRecord candidate, string idempotencyKey, CancellationToken ct);
    Task<LearnImageRecord?> FindAsync(Guid id, CancellationToken ct);
    /// <summary>Marks an open intent completed with its final object and writes audit in the same transaction; false if already completed.</summary>
    Task<bool> CompleteAsync(Guid id, string objectKey, int width, int height, CancellationToken ct);
}

public interface ILearnMediaService
{
    Task<AuthResult<LearnImageUploadIntentResponse>> CreateUploadIntentAsync(Guid actor, string? idempotencyKey, LearnImageUploadIntentRequest request, CancellationToken ct);
    Task<AuthResult<LearnImageResponse>> CompleteUploadAsync(Guid actor, Guid uploadId, string publicBaseUrl, CancellationToken ct);
    /// <summary>A short-lived signed URL of a completed image; 404 NOT_FOUND when it does not exist, 503 when storage is down.</summary>
    Task<AuthResult<string>> SignedImageUrlAsync(Guid imageId, CancellationToken ct);
}

public static class LearnMediaRules
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public static readonly string[] ContentTypes = ["image/jpeg", "image/png", "image/webp"];
    public static readonly TimeSpan UploadLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ReadUrlLifetime = TimeSpan.FromMinutes(10);
    public static string StagingKey(Guid id) => $"learn/staging/{id:N}";
    public static string ImageKey(Guid id) => $"learn/images/{id:N}";
    public static string PublicPath(Guid id) => $"/api/learn/media/images/{id}";

    public static Dictionary<string, string[]> Validate(LearnImageUploadIntentRequest? request)
    {
        var errors = new Dictionary<string, string[]>();
        if (request is null) { errors["body"] = ["Send fileName, contentType, sizeBytes and sha256."]; return errors; }
        var name = request.FileName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name.Any(char.IsControl)) errors["fileName"] = ["Use 1-255 characters without control characters."];
        if (request.ContentType is null || !ContentTypes.Contains(request.ContentType.Trim().ToLowerInvariant())) errors["contentType"] = ["Use image/jpeg, image/png or image/webp."];
        if (request.SizeBytes is < 1 or > MaxBytes) errors["sizeBytes"] = [$"Use 1-{MaxBytes} bytes (5 MB)."];
        if (request.Sha256 is null || request.Sha256.Length != 64 || !request.Sha256.All(Uri.IsHexDigit)) errors["sha256"] = ["Use the 64-character hex SHA-256 of the file."];
        return errors;
    }

    /// <summary>Hash of the normalized request, so the same Idempotency-Key with other input is a conflict.</summary>
    public static string InputHash(LearnImageUploadIntentRequest request) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        fileName = request.FileName.Trim(), contentType = request.ContentType.Trim().ToLowerInvariant(), sizeBytes = request.SizeBytes, sha256 = request.Sha256.ToLowerInvariant()
    }))));
}

/// <summary>
/// PlatformAdmin uploads a Learn image in two steps: an intent returns a presigned PUT to a private staging object; complete checks
/// the stored bytes (size, type, SHA-256, full decode) and copies them to an immutable object served at a stable public address.
/// </summary>
public sealed class LearnMediaService(ILearnMediaStore store, IStorageService storage, TimeProvider clock) : ILearnMediaService
{
    public async Task<AuthResult<LearnImageUploadIntentResponse>> CreateUploadIntentAsync(Guid actor, string? idempotencyKey, LearnImageUploadIntentRequest request, CancellationToken ct)
    {
        if (!BuildingCommandKey.IsValid(idempotencyKey))
            return AuthResult<LearnImageUploadIntentResponse>.Fail("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key must contain 1-128 printable non-whitespace characters.", 400);
        var errors = LearnMediaRules.Validate(request);
        if (errors.Count > 0) return AuthResult<LearnImageUploadIntentResponse>.Fail("VALIDATION_ERROR", "The image does not meet the upload rules.", 400, errors);
        var now = clock.GetUtcNow().UtcDateTime;
        var id = Guid.NewGuid();
        var candidate = new LearnImageRecord(id, actor, LearnMediaRules.InputHash(request), request.FileName.Trim(), request.ContentType.Trim().ToLowerInvariant(),
            request.SizeBytes, request.Sha256.ToLowerInvariant(), LearnMediaRules.StagingKey(id), null, null, null, now, now.Add(LearnMediaRules.UploadLifetime), null);
        var stored = await store.CreateOrGetIntentAsync(candidate, idempotencyKey!, ct);
        if (stored.InputHash != candidate.InputHash)
            return AuthResult<LearnImageUploadIntentResponse>.Fail("IDEMPOTENCY_KEY_CONFLICT", "This Idempotency-Key was used for another image.", 409);
        if (stored.CompletedAt is null && stored.ExpiresAt <= now)
            return AuthResult<LearnImageUploadIntentResponse>.Fail("LEARN_UPLOAD_EXPIRED", "This upload expired. Start again with a new Idempotency-Key.", 409);
        var remaining = stored.CompletedAt is null ? stored.ExpiresAt - now : LearnMediaRules.UploadLifetime;
        var (reachable, url) = await Storage(() => storage.GeneratePresignedUploadUrlAsync(stored.StagingKey, stored.ContentType, remaining, ct));
        return reachable ? AuthResult<LearnImageUploadIntentResponse>.Ok(new(stored.Id, url, new Dictionary<string, string> { ["Content-Type"] = stored.ContentType }, stored.ExpiresAt))
            : Unavailable<LearnImageUploadIntentResponse>();
    }

    public async Task<AuthResult<LearnImageResponse>> CompleteUploadAsync(Guid actor, Guid uploadId, string publicBaseUrl, CancellationToken ct)
    {
        var record = await store.FindAsync(uploadId, ct);
        // Another admin's upload is not found either: an upload belongs to whoever created it.
        if (record is null || record.CreatedBy != actor) return AuthResult<LearnImageResponse>.Fail("NOT_FOUND", "Upload not found.", 404);
        if (record.CompletedAt is not null) return AuthResult<LearnImageResponse>.Ok(Response(record, publicBaseUrl));
        if (record.ExpiresAt <= clock.GetUtcNow().UtcDateTime)
            return AuthResult<LearnImageResponse>.Fail("LEARN_UPLOAD_EXPIRED", "This upload expired. Start again with a new Idempotency-Key.", 409);
        var (reachable, metadata) = await Storage(() => storage.GetObjectMetadataAsync(record.StagingKey, ct));
        if (!reachable) return Unavailable<LearnImageResponse>();
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.ETag))
            return AuthResult<LearnImageResponse>.Fail("LEARN_UPLOAD_MISSING", "PUT the file to uploadUrl before completing.", 409);
        if (metadata.ContentLength != record.SizeBytes || !string.Equals(metadata.ContentType, record.ContentType, StringComparison.OrdinalIgnoreCase))
            return AuthResult<LearnImageResponse>.Fail("VALIDATION_ERROR", "The uploaded file does not match the declared size or content type.", 400);
        (reachable, var bytes) = await Storage(() => storage.ReadObjectAsync(record.StagingKey, LearnMediaRules.MaxBytes, metadata.ETag, ct));
        if (!reachable) return Unavailable<LearnImageResponse>();
        if (bytes is null) return AuthResult<LearnImageResponse>.Fail("LEARN_UPLOAD_CHANGED", "The upload changed while it was checked. Upload again.", 409);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != record.Sha256)
            return AuthResult<LearnImageResponse>.Fail("LEARN_IMAGE_INVALID", "The uploaded file does not match the declared sha256.", 422);
        if (ImageInspection.Inspect(bytes, record.ContentType, LearnMediaRules.MaxBytes) is not { } size)
            return AuthResult<LearnImageResponse>.Fail("LEARN_IMAGE_INVALID", $"The file must be a valid single-frame {record.ContentType} image of at most {ImageInspection.MaxSide} by {ImageInspection.MaxSide} pixels.", 422);
        var objectKey = LearnMediaRules.ImageKey(record.Id);
        (reachable, var copied) = await Storage(() => storage.CopyObjectIfUnchangedAsync(record.StagingKey, metadata.ETag, objectKey, record.ContentType, ct));
        if (!reachable) return Unavailable<LearnImageResponse>();
        if (!copied) return AuthResult<LearnImageResponse>.Fail("LEARN_UPLOAD_CHANGED", "The upload changed while it was checked. Upload again.", 409);
        if (!await store.CompleteAsync(record.Id, objectKey, size.Width, size.Height, ct))
        {
            // A concurrent complete won the same intent; both callers get the stored image.
            var winner = await store.FindAsync(record.Id, ct);
            return winner?.CompletedAt is not null ? AuthResult<LearnImageResponse>.Ok(Response(winner, publicBaseUrl))
                : AuthResult<LearnImageResponse>.Fail("LEARN_UPLOAD_EXPIRED", "This upload expired. Start again with a new Idempotency-Key.", 409);
        }
        // A leftover staging object is harmless: the image is already final.
        await Storage(async () => { await storage.DeleteObjectAsync(record.StagingKey, ct); return true; });
        return AuthResult<LearnImageResponse>.Ok(Response(record with { ObjectKey = objectKey, Width = size.Width, Height = size.Height }, publicBaseUrl));
    }

    public async Task<AuthResult<string>> SignedImageUrlAsync(Guid imageId, CancellationToken ct)
    {
        var record = await store.FindAsync(imageId, ct);
        if (record?.CompletedAt is null || record.ObjectKey is null) return AuthResult<string>.Fail("NOT_FOUND", "Image not found.", 404);
        var (reachable, url) = await Storage(() => storage.GeneratePresignedDownloadUrlAsync(record.ObjectKey, LearnMediaRules.ReadUrlLifetime, ct));
        return reachable ? AuthResult<string>.Ok(url) : Unavailable<string>();
    }

    private static LearnImageResponse Response(LearnImageRecord r, string publicBaseUrl) =>
        new(r.Id, publicBaseUrl.TrimEnd('/') + LearnMediaRules.PublicPath(r.Id), r.Width!.Value, r.Height!.Value, r.ContentType, r.SizeBytes);
    // Only storage failures become 503; database errors keep their own handling.
    private static async Task<(bool Reachable, T Value)> Storage<T>(Func<Task<T>> call)
    {
        try { return (true, await call()); }
        catch (Exception e) when (e is not OperationCanceledException) { return (false, default!); }
    }
    private static AuthResult<T> Unavailable<T>() => AuthResult<T>.Fail("LEARN_MEDIA_UNAVAILABLE", "Image storage is unavailable. Try again later.", 503);
}
