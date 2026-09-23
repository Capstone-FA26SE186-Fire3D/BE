using System.Text.Json;
using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Ifc;

// Persistence-only projection. StorageKey is never returned by the API.
public sealed record EditorPreviewSource(Guid BuildingId, Guid RevisionId, string RevisionStatus,
    Guid? ArtifactId, Guid? AttemptId, string? StorageKey, string? Sha256Hash,
    JsonElement? CoordinateTransform, JsonElement? Floors, JsonElement? SemanticMapping);
public sealed record EditorPreviewResponse(Guid BuildingId, Guid RevisionId, string RevisionStatus,
    string Status, Guid? ArtifactId, Guid? AttemptId, string? Sha256Hash,
    string? DownloadUrl, DateTimeOffset? ExpiresAt,
    JsonElement? CoordinateTransform, JsonElement? Floors, JsonElement? SemanticMapping);
public interface IEditorPreviewStore
{
    Task<EditorPreviewSource?> ReadAsync(Guid buildingId, Guid revisionId, Guid? tenant, CancellationToken ct);
}
public sealed record SignedDownload(string Url, DateTimeOffset ExpiresAt);
public interface IPreviewDownloadSigner
{
    Task<SignedDownload> SignAsync(string storageKey, CancellationToken ct);
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
        var ready = source.ArtifactId.HasValue && !string.IsNullOrWhiteSpace(source.StorageKey)
            && source.Sha256Hash is { Length: 64 } hash && hash.All(Uri.IsHexDigit)
            && source.CoordinateTransform is { ValueKind: JsonValueKind.Array } transform && transform.GetArrayLength() == 16
            && transform.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out var v) && double.IsFinite(v))
            && source.Floors is { ValueKind: JsonValueKind.Array }
            && source.SemanticMapping is { ValueKind: JsonValueKind.Object };
        var download = ready ? await signer.SignAsync(source.StorageKey!, ct) : null;
        return AuthResult<EditorPreviewResponse>.Ok(new(source.BuildingId, source.RevisionId, source.RevisionStatus,
            ready ? "Ready" : "NotReady", source.ArtifactId, source.AttemptId, source.Sha256Hash,
            download?.Url, download?.ExpiresAt, source.CoordinateTransform, source.Floors, source.SemanticMapping));
    }
}
