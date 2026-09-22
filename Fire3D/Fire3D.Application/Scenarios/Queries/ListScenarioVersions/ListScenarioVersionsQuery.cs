using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;

namespace Fire3D.Application.Scenarios.Queries.ListScenarioVersions;

public sealed record ListScenarioVersionsQuery(Guid ActorId, Guid ScenarioId, int Page = 1, int PageSize = 20)
    : IRequest<AuthResult<PageResponse<ScenarioVersionSummaryResponse>>>;

public sealed class ListScenarioVersionsQueryHandler(IAuthStore accounts, IScenarioReadStore store)
    : IRequestHandler<ListScenarioVersionsQuery, AuthResult<PageResponse<ScenarioVersionSummaryResponse>>>
{
    public async Task<AuthResult<PageResponse<ScenarioVersionSummaryResponse>>> Handle(
        ListScenarioVersionsQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.ScenarioId == Guid.Empty || request.Page is < 1 or > 100000 || request.PageSize is < 1 or > 100)
            return AuthResult<PageResponse<ScenarioVersionSummaryResponse>>.Fail(
                "VALIDATION_ERROR", "Scenario ID and valid pagination are required.", 400);

        var result = await store.ListScenarioVersionsAsync(
            request.ScenarioId, scope.Value!.OrganizationId, request.Page, request.PageSize, ct);
        return result is null
            ? AuthResult<PageResponse<ScenarioVersionSummaryResponse>>.Fail("NOT_FOUND", "Scenario not found or access denied.", 404)
            : AuthResult<PageResponse<ScenarioVersionSummaryResponse>>.Ok(result);
    }
}
