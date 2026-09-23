using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;

namespace Fire3D.Application.Scenarios.Queries.GetScenarioDraft;

public sealed record GetScenarioDraftQuery(Guid ActorId, Guid DraftId) : IRequest<AuthResult<ScenarioDraftResponse>>;

public sealed class GetScenarioDraftQueryHandler(IAuthStore accounts, IScenarioReadStore store)
    : IRequestHandler<GetScenarioDraftQuery, AuthResult<ScenarioDraftResponse>>
{
    public async Task<AuthResult<ScenarioDraftResponse>> Handle(GetScenarioDraftQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.DraftId == Guid.Empty)
            return AuthResult<ScenarioDraftResponse>.Fail("VALIDATION_ERROR", "Scenario draft ID is required.", 400);
        var result = await store.GetScenarioDraftAsync(request.DraftId, scope.Value!.OrganizationId, ct);
        return result is null
            ? AuthResult<ScenarioDraftResponse>.Fail("NOT_FOUND", "Scenario draft not found or access denied.", 404)
            : AuthResult<ScenarioDraftResponse>.Ok(result);
    }
}
