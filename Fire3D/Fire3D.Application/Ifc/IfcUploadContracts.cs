using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc.Commands.FinalizeUpload;
using Fire3D.Application.Ifc.Commands.InitiateUpload;

namespace Fire3D.Application.Ifc;

public sealed class IfcUploadOptions
{
    public bool Enabled { get; set; }
    public long? MaxBytes { get; set; }
    public bool CleanupEnabled { get; set; }
}
public sealed record IfcUploadIntent(Guid RevisionId, Guid BuildingId, Guid OrganizationId, Guid ActorId,
    string StagingKey, long Size, string Hash, string Filename, DateTime ExpiresAt, bool Completed);
public sealed record IfcUploadAttempt(Guid Id, string FinalKey);
public sealed record IfcObjectDigest(long Size, string Hash, string ETag);
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
