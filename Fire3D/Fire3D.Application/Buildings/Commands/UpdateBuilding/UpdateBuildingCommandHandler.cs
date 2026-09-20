using Fire3D.Application.Authentication;
using Fire3D.Domain.Entities;
using MediatR;

namespace Fire3D.Application.Buildings.Commands.UpdateBuilding;

internal sealed class UpdateBuildingCommandHandler(IBuildingStore store, TimeProvider clock)
    : IRequestHandler<UpdateBuildingCommand, AuthResult<BuildingResponse>>
{
    public async Task<AuthResult<BuildingResponse>> Handle(UpdateBuildingCommand command, CancellationToken ct)
    {
        var request = command.Request;
        var name = request.Name?.Trim();
        
        if (string.IsNullOrEmpty(name) || name.Length > 200 || request.TotalFloors < 1)
            return AuthResult<BuildingResponse>.Fail("VALIDATION_ERROR", "Name is required (max 200) and TotalFloors must be >= 1.", 400);

        var building = await store.FindBuildingAsync(command.BuildingId, command.OrganizationId, ct);
        if (building == null)
            return AuthResult<BuildingResponse>.Fail("NOT_FOUND", "Building not found.", 404);

        var now = clock.GetUtcNow().UtcDateTime;
        
        building.Name = name;
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
                ContactName = request.Contact.ContactName.Trim(),
                ContactRole = request.Contact.ContactRole?.Trim(),
                Phone = request.Contact.Phone?.Trim(),
                Email = request.Contact.Email?.Trim(),
                IsPrimary = request.Contact.IsPrimary,
                CreatedAt = now,
                UpdatedAt = now
            };
        }

        await store.UpdateBuildingAsync(building, location, contact, ct);
        await store.WriteAuditAsync(command.ActorId, command.OrganizationId, "buildings", building.Id, "Update", now, ct);

        // Fetch again to get the updated nested entities with their actual IDs
        var updatedBuilding = await store.FindBuildingAsync(command.BuildingId, command.OrganizationId, ct);
        
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
