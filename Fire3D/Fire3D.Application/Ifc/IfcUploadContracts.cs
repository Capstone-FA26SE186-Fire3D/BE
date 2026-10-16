using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc.Commands.FinalizeUpload;
using Fire3D.Application.Ifc.Commands.InitiateUpload;

namespace Fire3D.Application.Ifc;

public sealed class IfcUploadOptions
{
    public bool Enabled { get; set; }
    public long? MaxBytes { get; set; }
    public bool CleanupEnabled { get; set; }
    /// <summary>Minimum storage throughput assumed when completing an upload (two full reads plus a copy); scales the completion deadline.</summary>
    public long MinThroughputBytesPerSecond { get; set; } = 5L * 1024 * 1024;
    /// <summary>Upper bound for one completion attempt; a retry with the same input resumes from the tracked candidate.</summary>
    public int MaxCompletionSeconds { get; set; } = 900;
    /// <summary>Completion deadline: 30 s of fixed overhead plus two full reads of the object at the minimum throughput, capped.</summary>
    public TimeSpan CompletionTimeout(long size) =>
        TimeSpan.FromSeconds(Math.Min(Math.Max(30, MaxCompletionSeconds), 30 + 2d * size / Math.Max(1, MinThroughputBytesPerSecond)));
}
public sealed record IfcUploadIntent(Guid RevisionId, Guid BuildingId, Guid OrganizationId, Guid ActorId,
    string StagingKey, long Size, string Hash, string Filename, DateTime ExpiresAt, bool Completed);
public sealed record IfcUploadAttempt(Guid Id, string FinalKey);
/// <summary>StepHeader: the bytes start with the ISO-10303-21 (STEP/IFC) header, after an optional UTF-8 BOM and whitespace.</summary>
public sealed record IfcObjectDigest(long Size, string Hash, string ETag, bool StepHeader = true)
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
public sealed class IfcStorageFailure : Exception { public IfcStorageFailure(Exception inner) : base("IFC storage provider is temporarily unavailable.", inner) { } }
public interface IIfcUploadStore
{
    Task<AuthResult<IfcUploadIntent>> InitiateAsync(Guid actor, Guid building, InitiateIfcUploadRequest input, string key, CancellationToken ct);
    Task<AuthResult<IfcUploadIntent>> ReadAsync(Guid actor, Guid revision, FinalizeIfcUploadRequest input, CancellationToken ct);
    Task<AuthResult<IfcUploadAttempt>> ClaimAsync(Guid actor, Guid revision, FinalizeIfcUploadRequest input, string etag, CancellationToken ct);
    Task<AuthResult<bool>> AdoptAsync(Guid actor, Guid revision, Guid attempt, FinalizeIfcUploadRequest input, string etag, CancellationToken ct);
}
public interface IIfcUploadService
{
    Task<AuthResult<InitiateIfcUploadResponse>> InitiateAsync(Guid actor, Guid building, InitiateIfcUploadRequest input, string? key, CancellationToken ct);
    Task<AuthResult<bool>> CompleteAsync(Guid actor, Guid revision, FinalizeIfcUploadRequest input, CancellationToken ct);
}
