using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;

namespace Fire3D.Application.Scenarios.Queries.GetScenarioVersion;

public sealed record GetScenarioVersionQuery(Guid ActorId, Guid VersionId) : IRequest<AuthResult<ScenarioVersionDetailResponse>>;

public sealed class GetScenarioVersionQueryHandler(IAuthStore accounts, IScenarioReadStore store)
    : IRequestHandler<GetScenarioVersionQuery, AuthResult<ScenarioVersionDetailResponse>>
{
    public async Task<AuthResult<ScenarioVersionDetailResponse>> Handle(GetScenarioVersionQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.VersionId == Guid.Empty)
            return AuthResult<ScenarioVersionDetailResponse>.Fail("VALIDATION_ERROR", "Scenario version ID is required.", 400);

        var result = await store.GetScenarioVersionAsync(request.VersionId, scope.Value!.OrganizationId, ct);
        return result is null
            ? AuthResult<ScenarioVersionDetailResponse>.Fail("NOT_FOUND", "Scenario version not found or access denied.", 404)
            : AuthResult<ScenarioVersionDetailResponse>.Ok(result);
    }
}
