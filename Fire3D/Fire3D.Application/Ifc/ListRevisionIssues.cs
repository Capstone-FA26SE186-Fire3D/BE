using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Administration;
using MediatR;
namespace Fire3D.Application.Ifc;
public sealed record RevisionIssueResponse(Guid Id, Guid RevisionId, Guid ValidationRunId, Guid ProcessingAttemptId, Guid? ArtifactId, string IssueCode, string Severity, string Status, string Message, JsonElement Evidence, bool IsCurrentAttempt, DateTime CreatedAt);
public sealed record ListRevisionIssuesQuery(Guid ActorId, Guid RevisionId, int Page = 1, int PageSize = 20)
    : IRequest<AuthResult<PageResponse<RevisionIssueResponse>>>;
public sealed class ListRevisionIssuesHandler(IAuthStore accounts, IIfcReadStore store)
    : IRequestHandler<ListRevisionIssuesQuery, AuthResult<PageResponse<RevisionIssueResponse>>>
{
    public async Task<AuthResult<PageResponse<RevisionIssueResponse>>> Handle(ListRevisionIssuesQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.RevisionId == Guid.Empty || request.Page is < 1 or > 100000 || request.PageSize is < 1 or > 100)
            return AuthResult<PageResponse<RevisionIssueResponse>>.Fail("VALIDATION_ERROR", "Revision ID and valid pagination are required.", 400);
        var result = await store.ListRevisionIssuesAsync(request.RevisionId, scope.Value!.OrganizationId, request.Page, request.PageSize, ct);
        return result is null ? AuthResult<PageResponse<RevisionIssueResponse>>.Fail("NOT_FOUND", "Revision not found.", 404)
            : AuthResult<PageResponse<RevisionIssueResponse>>.Ok(result);
    }
}
