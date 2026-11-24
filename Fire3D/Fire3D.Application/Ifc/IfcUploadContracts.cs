using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc.Commands.FinalizeUpload;
using Fire3D.Application.Ifc.Commands.InitiateUpload;

namespace Fire3D.Application.Ifc;

public sealed class IfcUploadOptions
{
    /// <summary>Default upload limit (500 MiB) used by every environment that does not set IfcUpload:MaxBytes.</summary>
    public const long DefaultMaxBytes = 524_288_000;
    public bool Enabled { get; set; }
    /// <summary>Largest accepted IFC in bytes; initiate refuses a larger file. Published by GET /api/ifc/upload-limits.</summary>
    public long? MaxBytes { get; set; } = DefaultMaxBytes;
    public bool CleanupEnabled { get; set; }
    /// <summary>Minimum storage throughput assumed when completing an upload (two full reads plus a copy); scales the completion deadline.</summary>
    public long MinThroughputBytesPerSecond { get; set; } = 5L * 1024 * 1024;
    /// <summary>Upper bound for one completion attempt; a retry with the same input resumes from the tracked candidate.</summary>
    public int MaxCompletionSeconds { get; set; } = 900;
    /// <summary>Completion deadline: 30 s of fixed overhead plus two full reads of the object at the minimum throughput, capped.</summary>
    public TimeSpan CompletionTimeout(long size) =>
        TimeSpan.FromSeconds(Math.Min(Math.Max(30, MaxCompletionSeconds), 30 + 2d * size / Math.Max(1, MinThroughputBytesPerSecond)));
}
/// <summary>GET /api/ifc/upload-limits: what the web checks before an upload.</summary>
public sealed record IfcUploadLimitsResponse(bool Enabled, long MaxBytes, IReadOnlyList<string> AcceptedExtensions, IReadOnlyList<string> AcceptedSchemas,
    int DailyProcessLimitPerOrganization = IfcProcessingOptions.DefaultDailyLimitPerOrganization)
{
    public static IfcUploadLimitsResponse From(IfcUploadOptions options, IfcProcessingOptions? processing = null) =>
        new(options.Enabled && options.MaxBytes is > 0, options.MaxBytes ?? 0, [".ifc"], IfcStepSummary.SupportedSchemas,
            (processing ?? new()).DailyLimitPerOrganization);
}
public sealed record IfcUploadIntent(Guid RevisionId, Guid BuildingId, Guid OrganizationId, Guid ActorId,
    string StagingKey, long Size, string Hash, string Filename, DateTime ExpiresAt, bool Completed);
public sealed record IfcUploadAttempt(Guid Id, string FinalKey);
/// <summary>Step: what <see cref="IfcStepScanner"/> read while the object was hashed (STEP header, FILE_SCHEMA).</summary>
public sealed record IfcObjectDigest(long Size, string Hash, string ETag, IfcStepSummary Step)
{
    public static readonly byte[] Header = "ISO-10303-21;"u8.ToArray();
    /// <summary>True when the first bytes of the file are the STEP header; prefix holds at least the first 64 bytes or the whole file.</summary>
    public static bool IsStep(ReadOnlySpan<byte> prefix)
    {
        if (prefix.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) prefix = prefix[3..];
        while (!prefix.IsEmpty && prefix[0] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') prefix = prefix[1..];
        return prefix.StartsWith(Header);
    }
}
public interface IIfcSourceInspector
{
    Task<Fire3D.Application.Storage.StorageObjectMetadata?> MetadataAsync(string key, CancellationToken ct);
    Task<IfcObjectDigest?> InspectAsync(string key, string etag, long maxBytes, CancellationToken ct);
    Task<bool> CopyAsync(string key, string etag, string finalKey, CancellationToken ct);
}
/// <summary>
/// A content check refused the uploaded bytes (Code: IFC_UNSUPPORTED_FORMAT, IFC_UNSUPPORTED_SCHEMA, IFC_PRECHECK_FAILED). The SHA-256 already matched the
/// intent, so the same intent can never pass: the gate deletes the Draft revision and the intent and queues the staging object for cleanup.
/// </summary>
public sealed record IfcUploadRejection(string Code, string Message, IReadOnlyDictionary<string, string[]>? Details = null);
public sealed class IfcStorageFailure : Exception { public IfcStorageFailure(Exception inner) : base("IFC storage provider is temporarily unavailable.", inner) { } }
public interface IIfcUploadStore
{
    Task<AuthResult<IfcUploadIntent>> InitiateAsync(Guid actor, Guid building, InitiateIfcUploadRequest input, string key, CancellationToken ct);
    Task<AuthResult<IfcUploadIntent>> ReadAsync(Guid actor, Guid revision, FinalizeIfcUploadRequest input, CancellationToken ct);
    Task<AuthResult<IfcUploadAttempt>> ClaimAsync(Guid actor, Guid revision, FinalizeIfcUploadRequest input, string etag, CancellationToken ct);
    Task<AuthResult<bool>> AdoptAsync(Guid actor, Guid revision, Guid attempt, FinalizeIfcUploadRequest input, string etag, CancellationToken ct);
    /// <summary>Removes the Draft revision, intent and abandoned attempts of a rejected upload and audits the rejection.</summary>
    Task<AuthResult<bool>> RejectAsync(Guid actor, Guid revision, FinalizeIfcUploadRequest input, IfcUploadRejection rejection, CancellationToken ct);
}
public interface IIfcUploadService
{
    Task<AuthResult<InitiateIfcUploadResponse>> InitiateAsync(Guid actor, Guid building, InitiateIfcUploadRequest input, string? key, CancellationToken ct);
    Task<AuthResult<bool>> CompleteAsync(Guid actor, Guid revision, FinalizeIfcUploadRequest input, CancellationToken ct);
}
