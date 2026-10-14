using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Buildings.Commands.SetBuildingActive;

internal sealed class SetBuildingActiveCommandHandler(IBuildingStore store, IAuthStore accounts, TimeProvider clock)
    : IRequestHandler<SetBuildingActiveCommand, AuthResult<BuildingSummaryResponse>>
{
    public async Task<AuthResult<BuildingSummaryResponse>> Handle(SetBuildingActiveCommand command, CancellationToken ct)
    {
        if (!BuildingEtag.TryParse(command.IfMatch, out var expected))
            return string.IsNullOrWhiteSpace(command.IfMatch)
                ? AuthResult<BuildingSummaryResponse>.Fail("PRECONDITION_REQUIRED", "Send the Building ETag from GET in If-Match.", 428)
                : AuthResult<BuildingSummaryResponse>.Fail("VALIDATION_ERROR", "If-Match must be the quoted Building ETag.", 400);
        var scope = await BuildingAuthorization.ResolveScopeAsync(accounts, command.ActorId, command.OrganizationId, ct);
        if (!scope.IsSuccess) return AuthResult<BuildingSummaryResponse>.Fail(scope.Error!.Code, scope.Error.Message, scope.Error.Status);
        var building = await store.FindBuildingAsync(command.BuildingId, scope.Value, ct);
        if (building == null)
            return AuthResult<BuildingSummaryResponse>.Fail("NOT_FOUND", "Building not found.", 404);
        if (!await BuildingAuthorization.CanMutateAsync(accounts, command.ActorId, building.OrganizationId, ct))
            return AuthResult<BuildingSummaryResponse>.Fail("FORBIDDEN", "An active organization scope is required.", 403);
        // Already in the requested state: a retried DELETE succeeds without a write, whatever ETag it carries.
        if (building.IsActive != command.IsActive)
        {
            if (BuildingEtag.Version(building.UpdatedAt) != expected) return Stale();
            var outcome = await store.SetBuildingActiveWithAuditAsync(command.BuildingId, building.OrganizationId, command.IsActive, command.ActorId,
                clock.GetUtcNow().UtcDateTime, ct, expected);
            var current = await store.FindBuildingAsync(command.BuildingId, building.OrganizationId, ct);
            if (outcome != BuildingWriteOutcome.Written && (current is null || current.IsActive != command.IsActive))
                return outcome == BuildingWriteOutcome.Stale ? Stale()
                    : AuthResult<BuildingSummaryResponse>.Fail("BUILDING_MUTATION_FAILED", "Building could not be archived.", 409);
            building = current!;
        }

        var response = new BuildingSummaryResponse(building.Id, building.Name, building.BuildingType, building.TotalFloors, building.IsActive, building.CreatedAt, building.UpdatedAt);
        return AuthResult<BuildingSummaryResponse>.Ok(response);
    }

    private static AuthResult<BuildingSummaryResponse> Stale() =>
        AuthResult<BuildingSummaryResponse>.Fail("PRECONDITION_FAILED", "The Building changed since you read it. Reload it and retry.", 412);
}
