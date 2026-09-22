using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;

namespace Fire3D.Application.Scenarios.Queries.ListBuildingScenarios;

public sealed record ListBuildingScenariosQuery(Guid ActorId, Guid BuildingId, int Page = 1, int PageSize = 20)
    : IRequest<AuthResult<PageResponse<ScenarioSummaryResponse>>>;

public sealed class ListBuildingScenariosQueryHandler(IAuthStore accounts, IScenarioReadStore store)
    : IRequestHandler<ListBuildingScenariosQuery, AuthResult<PageResponse<ScenarioSummaryResponse>>>
{
    public async Task<AuthResult<PageResponse<ScenarioSummaryResponse>>> Handle(ListBuildingScenariosQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.BuildingId == Guid.Empty || request.Page is < 1 or > 100000 || request.PageSize is < 1 or > 100)
            return AuthResult<PageResponse<ScenarioSummaryResponse>>.Fail("VALIDATION_ERROR", "Building ID and valid pagination are required.", 400);

        var result = await store.ListBuildingScenariosAsync(request.BuildingId, scope.Value!.OrganizationId, request.Page, request.PageSize, ct);
        return result is null
            ? AuthResult<PageResponse<ScenarioSummaryResponse>>.Fail("NOT_FOUND", "Building not found or access denied.", 404)
            : AuthResult<PageResponse<ScenarioSummaryResponse>>.Ok(result);
    }
}
