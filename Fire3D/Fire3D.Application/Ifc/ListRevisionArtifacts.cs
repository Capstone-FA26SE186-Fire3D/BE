using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Administration;
using MediatR;
namespace Fire3D.Application.Ifc;
public sealed record RevisionArtifactResponse(Guid Id, Guid RevisionId, Guid JobId, Guid AttemptId, string ArtifactType, string Sha256Hash, JsonElement Metadata, bool IsRuntimeReady, bool IsCurrentAttempt, DateTime CreatedAt);
public sealed record ListRevisionArtifactsQuery(Guid ActorId, Guid RevisionId, int Page = 1, int PageSize = 20)
    : IRequest<AuthResult<PageResponse<RevisionArtifactResponse>>>;
public sealed class ListRevisionArtifactsHandler(IAuthStore accounts, IIfcReadStore store)
    : IRequestHandler<ListRevisionArtifactsQuery, AuthResult<PageResponse<RevisionArtifactResponse>>>
{
    public async Task<AuthResult<PageResponse<RevisionArtifactResponse>>> Handle(ListRevisionArtifactsQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.RevisionId == Guid.Empty || request.Page is < 1 or > 100000 || request.PageSize is < 1 or > 100)
            return AuthResult<PageResponse<RevisionArtifactResponse>>.Fail("VALIDATION_ERROR", "Revision ID and valid pagination are required.", 400);
        var result = await store.ListRevisionArtifactsAsync(request.RevisionId, scope.Value!.OrganizationId, request.Page, request.PageSize, ct);
        return result is null ? AuthResult<PageResponse<RevisionArtifactResponse>>.Fail("NOT_FOUND", "Revision not found.", 404)
            : AuthResult<PageResponse<RevisionArtifactResponse>>.Ok(result);
    }
}
