using Fire3D.Application.Authentication;
using Fire3D.Domain.Entities;
using MediatR;

namespace Fire3D.Application.Buildings.Commands.UpdateBuilding;

internal sealed class UpdateBuildingCommandHandler(IBuildingStore store, IAuthStore accounts, TimeProvider clock)
    : IRequestHandler<UpdateBuildingCommand, AuthResult<BuildingResponse>>
{
    public async Task<AuthResult<BuildingResponse>> Handle(UpdateBuildingCommand command, CancellationToken ct)
    {
        if (!BuildingEtag.TryParse(command.IfMatch, out var expected))
            return string.IsNullOrWhiteSpace(command.IfMatch)
                ? AuthResult<BuildingResponse>.Fail("PRECONDITION_REQUIRED", "Send the Building ETag from GET in If-Match.", 428)
                : AuthResult<BuildingResponse>.Fail("VALIDATION_ERROR", "If-Match must be the quoted Building ETag.", 400);
        var request = command.Request;
        var name = request.Name?.Trim();

        var errors = BuildingValidation.Validate(name, request.BuildingType, request.TotalFloors, request.Location, request.Contact);
        if (errors.Count > 0)
            return AuthResult<BuildingResponse>.Fail("VALIDATION_ERROR", "Building validation failed.", 400, errors);
        var scope = await BuildingAuthorization.ResolveScopeAsync(accounts, command.ActorId, command.OrganizationId, ct);
        if (!scope.IsSuccess) return AuthResult<BuildingResponse>.Fail(scope.Error!.Code, scope.Error.Message, scope.Error.Status);
        var building = await store.FindBuildingAsync(command.BuildingId, scope.Value, ct);
        // Archived Buildings stay readable in the console but are no longer editable (same 404 as revision/IFC routes).
        if (building == null || !building.IsActive)
            return AuthResult<BuildingResponse>.Fail("NOT_FOUND", "Building not found.", 404);
        if (!await BuildingAuthorization.CanMutateAsync(accounts, command.ActorId, building.OrganizationId, ct))
            return AuthResult<BuildingResponse>.Fail("FORBIDDEN", "An active organization scope is required.", 403);
        if (BuildingEtag.Version(building.UpdatedAt) != expected) return Stale();
        var now = clock.GetUtcNow().UtcDateTime;

        building.Name = name!;
        building.BuildingType = request.BuildingType?.Trim();
        building.TotalFloors = request.TotalFloors;
        building.UpdatedAt = now;

        BuildingLocation? location = null;
        if (request.Location != null)
        {
            location = new BuildingLocation
            {
                Id = Guid.NewGuid(),
                BuildingId = building.Id,
                Address = request.Location.Address?.Trim(),
                City = request.Location.City?.Trim(),
                District = request.Location.District?.Trim(),
                Latitude = request.Location.Latitude,
                Longitude = request.Location.Longitude,
                Geojson = request.Location.Geojson,
                CreatedAt = now,
                UpdatedAt = now
            };
        }

        BuildingContact? contact = null;
        if (request.Contact != null)
        {
            contact = new BuildingContact
            {
                Id = Guid.NewGuid(),
                BuildingId = building.Id,
                ContactName = request.Contact.ContactName?.Trim() ?? string.Empty,
                ContactRole = request.Contact.ContactRole?.Trim(),
                Phone = request.Contact.Phone?.Trim(),
                Email = request.Contact.Email?.Trim(),
                IsPrimary = request.Contact.IsPrimary,
                CreatedAt = now,
                UpdatedAt = now
            };
        }

        switch (await store.UpdateBuildingWithAuditAsync(building, location, contact, command.ActorId, now, ct, expected))
        {
            case BuildingWriteOutcome.Stale: return Stale();
            case BuildingWriteOutcome.Written: break;
            default: return AuthResult<BuildingResponse>.Fail("BUILDING_MUTATION_FAILED", "Building could not be updated.", 409);
        }

        // Fetch again to get the updated nested entities with their actual IDs
        var updatedBuilding = await store.FindBuildingAsync(command.BuildingId, building.OrganizationId, ct);
        return AuthResult<BuildingResponse>.Ok(BuildingMapping.ToResponse(updatedBuilding!));
    }

    private static AuthResult<BuildingResponse> Stale() =>
        AuthResult<BuildingResponse>.Fail("PRECONDITION_FAILED", "The Building changed since you read it. Reload it and retry.", 412);
}
