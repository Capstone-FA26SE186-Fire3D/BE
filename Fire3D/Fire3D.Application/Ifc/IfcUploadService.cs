using System.Security.Cryptography;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc.Commands.FinalizeUpload;
using Fire3D.Application.Ifc.Commands.InitiateUpload;
using Fire3D.Application.Storage;
using Microsoft.Extensions.Options;

namespace Fire3D.Application.Ifc;

public sealed class IfcUploadService(IIfcUploadStore store, IStorageService storage, IIfcSourceInspector inspector,
    IOptions<IfcUploadOptions> options, TimeProvider clock) : IIfcUploadService
{
    public async Task<AuthResult<InitiateIfcUploadResponse>> InitiateAsync(Guid actor, Guid building, InitiateIfcUploadRequest input, string? key, CancellationToken ct)
    {
        var errors = new Dictionary<string,string[]>();
        if (!Sha(input.Sha256Hash)) errors["sha256Hash"] = ["Expected SHA-256 must contain exactly 64 hexadecimal characters."];
        if (input.FileSizeBytes <= 0 || (options.Value.MaxBytes.HasValue && input.FileSizeBytes > options.Value.MaxBytes))
            errors["fileSizeBytes"] = ["fileSizeBytes must be positive and no larger than the configured IFC limit."];
        if (string.IsNullOrWhiteSpace(input.OriginalFilename) || input.OriginalFilename.Length > 255 || !input.OriginalFilename.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase) || input.OriginalFilename.Any(char.IsControl))
            errors["originalFilename"] = ["A filename ending in .ifc with at most 255 characters is required."];
        if (string.IsNullOrWhiteSpace(input.VersionLabel) || input.VersionLabel.Trim().Length > 100)
            errors["versionLabel"] = ["versionLabel must contain 1–100 characters."];
        if (errors.Count > 0) return AuthResult<InitiateIfcUploadResponse>.Fail("VALIDATION_ERROR", "IFC upload validation failed.", 400, errors);
        if (!options.Value.Enabled || options.Value.MaxBytes is not > 0)
            return AuthResult<InitiateIfcUploadResponse>.Fail("IFC_UPLOAD_DISABLED", "IFC upload is not configured.", 503);
        if (!Key(key)) return AuthResult<InitiateIfcUploadResponse>.Fail("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key must contain 1–128 printable non-whitespace characters.", 400);
        input = input with { Sha256Hash = input.Sha256Hash!.ToLowerInvariant(), OriginalFilename = input.OriginalFilename.Trim(), VersionLabel = input.VersionLabel.Trim() };
        var saved = await store.InitiateAsync(actor, building, input, key!, ct);
        if (!saved.IsSuccess) return new(default, saved.Error);
        var intent = saved.Value!;
        var ttl = intent.ExpiresAt - clock.GetUtcNow().UtcDateTime;
        if (ttl <= TimeSpan.Zero) return AuthResult<InitiateIfcUploadResponse>.Fail("IFC_UPLOAD_EXPIRED", "Upload intent has expired.", 410);
        if (intent.Completed) return AuthResult<InitiateIfcUploadResponse>.Fail("IFC_UPLOAD_ALREADY_COMPLETED", "Upload was already completed.", 409);
        var url = await storage.GeneratePresignedUploadUrlAsync(intent.StagingKey, "application/octet-stream", ttl, ct);
        return AuthResult<InitiateIfcUploadResponse>.Ok(new(intent.RevisionId, url, intent.StagingKey));
    }

    public async Task<AuthResult<bool>> CompleteAsync(Guid actor, Guid revision, FinalizeIfcUploadRequest input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Sha(input.Sha256Hash)) errors["sha256Hash"] = ["Expected SHA-256 must contain exactly 64 hexadecimal characters."];
        if (input.FileSizeBytes <= 0) errors["fileSizeBytes"] = ["fileSizeBytes must be positive."];
        if (string.IsNullOrWhiteSpace(input.ObjectKey) || input.ObjectKey.Length > 1024 || input.ObjectKey.Any(char.IsControl))
            errors["objectKey"] = ["Use the objectKey returned by upload initiation."];
        if (string.IsNullOrWhiteSpace(input.OriginalFilename) || input.OriginalFilename.Length > 255 || input.OriginalFilename.Any(char.IsControl))
            errors["originalFilename"] = ["originalFilename must contain 1-255 characters without control characters."];
        if (input.MimeType != "application/octet-stream") errors["mimeType"] = ["mimeType must be application/octet-stream."];
        if (errors.Count > 0) return AuthResult<bool>.Fail("VALIDATION_ERROR", "IFC completion validation failed.", 400, errors);
        input = input with { Sha256Hash = input.Sha256Hash.ToLowerInvariant(), OriginalFilename = input.OriginalFilename.Trim() };
        // Persisted ownership/input is checked BEFORE any S3 access, including replay.
        var authorized = await store.ReadAsync(actor, revision, input, ct);
        if (!authorized.IsSuccess) return new(default, authorized.Error);
        var intent = authorized.Value!;
        if (intent.Completed) return AuthResult<bool>.Ok(true);
        if (intent.ExpiresAt <= clock.GetUtcNow().UtcDateTime) return AuthResult<bool>.Fail("IFC_UPLOAD_EXPIRED", "Upload intent has expired.", 410);
        if (options.Value.MaxBytes is not > 0 || intent.Size > options.Value.MaxBytes)
            return AuthResult<bool>.Fail("IFC_UPLOAD_DISABLED", "IFC upload limit is not configured for this source.", 503);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var metadata = await inspector.MetadataAsync(intent.StagingKey, timeout.Token);
            if (metadata is null || metadata.ContentLength != intent.Size || string.IsNullOrWhiteSpace(metadata.ETag))
                return AuthResult<bool>.Fail("IFC_SOURCE_MISMATCH", "Object size does not match the upload intent.", 422);
            var digest = await inspector.InspectAsync(intent.StagingKey, metadata.ETag, intent.Size, timeout.Token);
            if (digest is null || !Sha(digest.Hash) || digest.Size != intent.Size || digest.ETag != metadata.ETag || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(digest.Hash), Convert.FromHexString(intent.Hash)))
                return AuthResult<bool>.Fail("IFC_SOURCE_MISMATCH", "Stored object changed or its SHA-256 does not match the upload intent.", 422);
            var claim = await store.ClaimAsync(actor, revision, input, digest.ETag, ct);
            if (!claim.IsSuccess) return new(default, claim.Error);
            // Candidate key is durable before copy. Every attempt owns a different final key.
            if (!await inspector.CopyAsync(intent.StagingKey, digest.ETag, claim.Value!.FinalKey, timeout.Token))
                return AuthResult<bool>.Fail("IFC_SOURCE_CHANGED", "Source object changed after inspection. Upload again and retry.", 409);
            // ETags are provider concurrency identifiers, not content hashes. Verify copied bytes as well.
            var copied = await inspector.MetadataAsync(claim.Value.FinalKey, timeout.Token);
            if (copied is null || copied.ContentLength != intent.Size || string.IsNullOrWhiteSpace(copied.ETag))
                return AuthResult<bool>.Fail("IFC_SOURCE_MISMATCH", "Copied source could not be verified.", 422);
            var finalDigest = await inspector.InspectAsync(claim.Value.FinalKey, copied.ETag, intent.Size, timeout.Token);
            if (finalDigest is null || !Sha(finalDigest.Hash) || finalDigest.Size != intent.Size || finalDigest.ETag != copied.ETag ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(finalDigest.Hash), Convert.FromHexString(intent.Hash)))
                return AuthResult<bool>.Fail("IFC_SOURCE_MISMATCH", "Copied source hash does not match the intent.", 422);
            return await store.AdoptAsync(actor, revision, claim.Value.Id, input, digest.ETag, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return AuthResult<bool>.Fail("IFC_STORAGE_UNAVAILABLE", "Storage timed out. Retry with the same input; the candidate remains tracked.", 503);
        }
        catch (IfcStorageFailure)
        {
            return AuthResult<bool>.Fail("IFC_STORAGE_UNAVAILABLE", "Storage is temporarily unavailable. Retry with the same input.", 503);
        }
    }
    private static bool Sha(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool Key(string? key) => key is { Length: > 0 and <= 128 } && !key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
}
