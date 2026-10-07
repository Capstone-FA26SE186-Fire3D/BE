using Fire3D.Application.Authentication;
using Fire3D.Domain.Entities;
using MediatR;

namespace Fire3D.Application.Buildings.Commands.UpdateBuilding;

internal sealed class UpdateBuildingCommandHandler(IBuildingStore store, IAuthStore accounts, TimeProvider clock)
    : IRequestHandler<UpdateBuildingCommand, AuthResult<BuildingResponse>>
{
    public async Task<AuthResult<BuildingResponse>> Handle(UpdateBuildingCommand command, CancellationToken ct)
    {
        var request = command.Request;
        var name = request.Name?.Trim();
        
        var errors = BuildingValidation.Validate(name, request.BuildingType, request.TotalFloors, request.Location, request.Contact);
        if (errors.Count > 0)
            return AuthResult<BuildingResponse>.Fail("VALIDATION_ERROR", "Building validation failed.", 400, errors);
        var scope = await BuildingAuthorization.ResolveScopeAsync(accounts, command.ActorId, command.OrganizationId, ct);
        if (!scope.IsSuccess) return AuthResult<BuildingResponse>.Fail(scope.Error!.Code, scope.Error.Message, scope.Error.Status);
        var building = await store.FindBuildingAsync(command.BuildingId, scope.Value, ct);
        if (building == null)
            return AuthResult<BuildingResponse>.Fail("NOT_FOUND", "Building not found.", 404);
        if (!await BuildingAuthorization.CanMutateAsync(accounts, command.ActorId, building.OrganizationId, ct))
            return AuthResult<BuildingResponse>.Fail("FORBIDDEN", "An active organization scope is required.", 403);
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

        if (!await store.UpdateBuildingWithAuditAsync(building, location, contact, command.ActorId, now, ct))
            return AuthResult<BuildingResponse>.Fail("BUILDING_MUTATION_FAILED", "Building could not be updated.", 409);

        // Fetch again to get the updated nested entities with their actual IDs
        var updatedBuilding = await store.FindBuildingAsync(command.BuildingId, building.OrganizationId, ct);
        
        BuildingLocationResponse? locationResponse = updatedBuilding!.BuildingLocation != null 
            ? new BuildingLocationResponse(updatedBuilding.BuildingLocation.Id, updatedBuilding.BuildingLocation.Address, updatedBuilding.BuildingLocation.City, updatedBuilding.BuildingLocation.District, updatedBuilding.BuildingLocation.Latitude, updatedBuilding.BuildingLocation.Longitude, updatedBuilding.BuildingLocation.Geojson) 
            : null;
            
        BuildingContactResponse? contactResponse = updatedBuilding.BuildingContact != null 
            ? new BuildingContactResponse(updatedBuilding.BuildingContact.Id, updatedBuilding.BuildingContact.ContactName, updatedBuilding.BuildingContact.ContactRole, updatedBuilding.BuildingContact.Phone, updatedBuilding.BuildingContact.Email, updatedBuilding.BuildingContact.IsPrimary) 
            : null;

        var response = new BuildingResponse(updatedBuilding.Id, updatedBuilding.Name, updatedBuilding.BuildingType, updatedBuilding.TotalFloors, updatedBuilding.IsActive, updatedBuilding.OrganizationId, updatedBuilding.CreatedAt, updatedBuilding.UpdatedAt, locationResponse, contactResponse);
        return AuthResult<BuildingResponse>.Ok(response);
    }
}
