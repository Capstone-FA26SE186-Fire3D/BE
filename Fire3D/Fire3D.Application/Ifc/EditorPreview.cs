using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Authentication;
using Fire3D.Application.Editor;
using MediatR;

namespace Fire3D.Application.Ifc;

// Persistence-only projection. StorageKey is never returned by the API.
public sealed record EditorPreviewSource(Guid BuildingId, Guid RevisionId, string RevisionStatus,
    Guid? ArtifactId, Guid? AttemptId, string? StorageKey, string? Sha256Hash, JsonElement? Metadata, long? SizeBytes = null);
/// <summary>
/// Status: Ready (versioned metadata valid and object verified), NotReady (no accepted Geometry artifact or object
/// not yet available) or ReprocessRequired (artifact exists but its metadata is legacy or fails the editor contract).
/// Coordinates are only returned when the metadata satisfies <see cref="EditorContract.V1"/>.
/// </summary>
public sealed record EditorPreviewResponse(Guid BuildingId, Guid RevisionId, string RevisionStatus,
    string Status, Guid? ArtifactId, Guid? AttemptId, string? Sha256Hash,
    string? DownloadUrl, DateTimeOffset? ExpiresAt,
    JsonElement? CoordinateTransform, JsonElement? Floors, JsonElement? SemanticMapping,
    string? SchemaVersion = null, string? Units = null, string? UpAxis = null, string? Handedness = null);
public sealed record RevisionFloorsResponse(Guid RevisionId, Guid BuildingId, string Status, Guid? ArtifactId,
    string? Sha256Hash, string? SchemaVersion, JsonElement? Floors);
public interface IEditorPreviewStore
{
    Task<EditorPreviewSource?> ReadAsync(Guid buildingId, Guid revisionId, Guid? tenant, CancellationToken ct);
    /// <summary>Same accepted-artifact projection addressed by revision only.</summary>
    Task<EditorPreviewSource?> ReadByRevisionAsync(Guid revisionId, Guid? tenant, CancellationToken ct);
}
public sealed record SignedDownload(string Url, DateTimeOffset ExpiresAt);
public sealed class PreviewStorageUnavailableException(Exception inner) : Exception("Preview storage is unavailable.", inner);
public interface IPreviewDownloadSigner
{
    Task<SignedDownload?> SignAsync(string storageKey, long expectedSizeBytes, CancellationToken ct);
}

public static class EditorPreviewMetadata
{
    /// <summary>NotReady when no artifact; ReprocessRequired unless the stored metadata passes the versioned contract.</summary>
    public static string Classify(EditorPreviewSource source) =>
        source.ArtifactId is null ? "NotReady"
        : source.Metadata is { ValueKind: JsonValueKind.Object } metadata
          && EditorContract.DeclaredVersion(JsonNode.Parse(metadata.GetRawText())) is { } version && EditorContract.IsSupported(version)
          && GeometryMetadataValidator.Validate(JsonNode.Parse(metadata.GetRawText())).Count == 0 ? "Valid" : "ReprocessRequired";

    public static JsonElement? Field(EditorPreviewSource source, string name) =>
        source.Metadata is { ValueKind: JsonValueKind.Object } m && m.TryGetProperty(name, out var value) ? value.Clone() : null;
}

public sealed record GetEditorPreviewQuery(Guid ActorId, Guid BuildingId, Guid RevisionId)
    : IRequest<AuthResult<EditorPreviewResponse>>;
public sealed class GetEditorPreviewHandler(IAuthStore accounts, IEditorPreviewStore store, IPreviewDownloadSigner signer)
    : IRequestHandler<GetEditorPreviewQuery, AuthResult<EditorPreviewResponse>>
{
    public async Task<AuthResult<EditorPreviewResponse>> Handle(GetEditorPreviewQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.BuildingId == Guid.Empty || request.RevisionId == Guid.Empty)
            return AuthResult<EditorPreviewResponse>.Fail("VALIDATION_ERROR", "buildingId and revisionId are required.", 400);
        var source = await store.ReadAsync(request.BuildingId, request.RevisionId, scope.Value!.OrganizationId, ct);
        if (source is null) return AuthResult<EditorPreviewResponse>.Fail("NOT_FOUND", "Revision not found in this building.", 404);
        var classification = EditorPreviewMetadata.Classify(source);
        var ready = classification == "Valid" && source.SizeBytes > 0 && !string.IsNullOrWhiteSpace(source.StorageKey)
            && source.Sha256Hash is { Length: 64 } hash && hash.All(Uri.IsHexDigit);
        SignedDownload? download;
        try { download = ready ? await signer.SignAsync(source.StorageKey!, source.SizeBytes!.Value, ct) : null; }
        catch (PreviewStorageUnavailableException)
        {
            return AuthResult<EditorPreviewResponse>.Fail("PREVIEW_STORAGE_UNAVAILABLE", "Preview storage is temporarily unavailable.", 503);
        }
        var valid = classification == "Valid";
        string? Text(string name) => valid ? EditorPreviewMetadata.Field(source, name)?.GetString() : null;
        return AuthResult<EditorPreviewResponse>.Ok(new(source.BuildingId, source.RevisionId, source.RevisionStatus,
            classification == "ReprocessRequired" ? "ReprocessRequired" : download is not null ? "Ready" : "NotReady",
            source.ArtifactId, source.AttemptId, source.Sha256Hash, download?.Url, download?.ExpiresAt,
            valid ? EditorPreviewMetadata.Field(source, "coordinateTransform") : null,
            valid ? EditorPreviewMetadata.Field(source, "floors") : null,
            valid ? EditorPreviewMetadata.Field(source, "semanticMapping") : null,
            Text("schemaVersion"), Text("units"), Text("upAxis"), Text("handedness")));
    }
}

public sealed record GetRevisionFloorsQuery(Guid ActorId, Guid RevisionId) : IRequest<AuthResult<RevisionFloorsResponse>>;
public sealed class GetRevisionFloorsHandler(IAuthStore accounts, IEditorPreviewStore store)
    : IRequestHandler<GetRevisionFloorsQuery, AuthResult<RevisionFloorsResponse>>
{
    public async Task<AuthResult<RevisionFloorsResponse>> Handle(GetRevisionFloorsQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.RevisionId == Guid.Empty)
            return AuthResult<RevisionFloorsResponse>.Fail("VALIDATION_ERROR", "revisionId is required.", 400);
        var source = await store.ReadByRevisionAsync(request.RevisionId, scope.Value!.OrganizationId, ct);
        if (source is null) return AuthResult<RevisionFloorsResponse>.Fail("NOT_FOUND", "Revision not found.", 404);
        var classification = EditorPreviewMetadata.Classify(source);
        var valid = classification == "Valid";
        return AuthResult<RevisionFloorsResponse>.Ok(new(source.RevisionId, source.BuildingId, valid ? "Ready" : classification,
            source.ArtifactId, source.Sha256Hash, valid ? EditorPreviewMetadata.Field(source, "schemaVersion")?.GetString() : null,
            valid ? EditorPreviewMetadata.Field(source, "floors") : null));
    }
}
