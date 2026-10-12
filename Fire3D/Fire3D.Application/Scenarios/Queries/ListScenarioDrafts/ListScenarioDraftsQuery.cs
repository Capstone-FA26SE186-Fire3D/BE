using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;

namespace Fire3D.Application.Scenarios.Queries.ListScenarioDrafts;

public sealed record ListScenarioDraftsQuery(Guid ActorId, Guid ScenarioId, int Page = 1, int PageSize = 20)
    : IRequest<AuthResult<PageResponse<ScenarioDraftSummaryResponse>>>;

public sealed class ListScenarioDraftsQueryHandler(IAuthStore accounts, IScenarioReadStore store)
    : IRequestHandler<ListScenarioDraftsQuery, AuthResult<PageResponse<ScenarioDraftSummaryResponse>>>
{
    public async Task<AuthResult<PageResponse<ScenarioDraftSummaryResponse>>> Handle(ListScenarioDraftsQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.ScenarioId == Guid.Empty || request.Page is < 1 or > 100000 || request.PageSize is < 1 or > 100)
            return AuthResult<PageResponse<ScenarioDraftSummaryResponse>>.Fail("VALIDATION_ERROR", "Scenario ID and valid pagination are required.", 400);
        var result = await store.ListScenarioDraftsAsync(request.ScenarioId, scope.Value!.OrganizationId, request.Page, request.PageSize, ct);
        return result is null
            ? AuthResult<PageResponse<ScenarioDraftSummaryResponse>>.Fail("NOT_FOUND", "Scenario not found or access denied.", 404)
            : AuthResult<PageResponse<ScenarioDraftSummaryResponse>>.Ok(result);
    }
}
