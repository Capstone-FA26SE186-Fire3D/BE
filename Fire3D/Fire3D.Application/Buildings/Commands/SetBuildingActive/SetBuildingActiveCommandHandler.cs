using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Buildings.Commands.SetBuildingActive;

internal sealed class SetBuildingActiveCommandHandler(IBuildingStore store, IAuthStore accounts, TimeProvider clock)
    : IRequestHandler<SetBuildingActiveCommand, AuthResult<BuildingSummaryResponse>>
{
    public async Task<AuthResult<BuildingSummaryResponse>> Handle(SetBuildingActiveCommand command, CancellationToken ct)
    {
        if (!await BuildingAuthorization.CanMutateAsync(accounts, command.ActorId, command.OrganizationId, ct))
            return AuthResult<BuildingSummaryResponse>.Fail("FORBIDDEN", "An active organization scope is required.", 403);
        var building = await store.FindBuildingAsync(command.BuildingId, command.OrganizationId, ct);
        if (building == null)
            return AuthResult<BuildingSummaryResponse>.Fail("NOT_FOUND", "Building not found.", 404);

        if (building.IsActive != command.IsActive)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (!await store.SetBuildingActiveWithAuditAsync(command.BuildingId, command.OrganizationId, command.IsActive, command.ActorId, now, ct))
            {
                var concurrent = await store.FindBuildingAsync(command.BuildingId, command.OrganizationId, ct);
                if (concurrent is null || concurrent.IsActive != command.IsActive)
                    return AuthResult<BuildingSummaryResponse>.Fail("BUILDING_MUTATION_FAILED", "Building could not be archived.", 409);
                building = concurrent;
            }
            
            else { building.IsActive = command.IsActive; building.UpdatedAt = now; }
        }

        var response = new BuildingSummaryResponse(building.Id, building.Name, building.BuildingType, building.TotalFloors, building.IsActive, building.CreatedAt);
        return AuthResult<BuildingSummaryResponse>.Ok(response);
    }
}
