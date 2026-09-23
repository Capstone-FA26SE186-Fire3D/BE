using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;

namespace Fire3D.Application.Scenarios.Queries.GetScenario;

public sealed record GetScenarioQuery(Guid ActorId, Guid ScenarioId) : IRequest<AuthResult<ScenarioDetailResponse>>;

public sealed class GetScenarioQueryHandler(IAuthStore accounts, IScenarioReadStore store)
    : IRequestHandler<GetScenarioQuery, AuthResult<ScenarioDetailResponse>>
{
    public async Task<AuthResult<ScenarioDetailResponse>> Handle(GetScenarioQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.ScenarioId == Guid.Empty)
            return AuthResult<ScenarioDetailResponse>.Fail("VALIDATION_ERROR", "Scenario ID is required.", 400);

        var result = await store.GetScenarioAsync(request.ScenarioId, scope.Value!.OrganizationId, ct);
        return result is null
            ? AuthResult<ScenarioDetailResponse>.Fail("NOT_FOUND", "Scenario not found or access denied.", 404)
            : AuthResult<ScenarioDetailResponse>.Ok(result);
    }
}
