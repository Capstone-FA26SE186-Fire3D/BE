using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Buildings.Queries.GetBuilding;

internal sealed class GetBuildingQueryHandler(IBuildingStore store, IAuthStore accounts)
    : IRequestHandler<GetBuildingQuery, AuthResult<BuildingResponse>>
{
    public async Task<AuthResult<BuildingResponse>> Handle(GetBuildingQuery query, CancellationToken ct)
    {
        var scope = await BuildingAuthorization.ResolveScopeAsync(accounts, query.ActorId, query.OrganizationId, ct);
        if (!scope.IsSuccess) return AuthResult<BuildingResponse>.Fail(scope.Error!.Code, scope.Error.Message, scope.Error.Status, scope.Error.Errors);
        var building = await store.FindBuildingAsync(query.BuildingId, scope.Value, ct);
        if (building == null)
            return AuthResult<BuildingResponse>.Fail("NOT_FOUND", "Building not found.", 404);
        return AuthResult<BuildingResponse>.Ok(BuildingMapping.ToResponse(building));
    }
}
