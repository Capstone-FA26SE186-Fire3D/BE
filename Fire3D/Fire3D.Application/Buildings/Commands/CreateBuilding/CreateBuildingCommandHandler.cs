using Fire3D.Application.Authentication;
using Fire3D.Domain.Entities;
using MediatR;

namespace Fire3D.Application.Buildings.Commands.CreateBuilding;

internal sealed class CreateBuildingCommandHandler(IBuildingStore store, TimeProvider clock)
    : IRequestHandler<CreateBuildingCommand, AuthResult<BuildingResponse>>
{
    public async Task<AuthResult<BuildingResponse>> Handle(CreateBuildingCommand command, CancellationToken ct)
    {
        var request = command.Request;
        var name = request.Name?.Trim();
        
        if (string.IsNullOrEmpty(name) || name.Length > 200 || request.TotalFloors < 1)
            return AuthResult<BuildingResponse>.Fail("VALIDATION_ERROR", "Name is required (max 200) and TotalFloors must be >= 1.", 400);

        var now = clock.GetUtcNow().UtcDateTime;
        var buildingId = Guid.NewGuid();
        
        var building = new Building
        {
            Id = buildingId,
            OrganizationId = command.OrganizationId,
            Name = name,
            BuildingType = request.BuildingType?.Trim(),
            TotalFloors = request.TotalFloors,
            IsActive = true,
            CreatedBy = command.ActorId,
            CreatedAt = now,
            UpdatedAt = now
        };

        BuildingLocation? location = null;
        if (request.Location != null)
        {
            location = new BuildingLocation
            {
                Id = Guid.NewGuid(),
                BuildingId = buildingId,
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
                BuildingId = buildingId,
                ContactName = request.Contact.ContactName.Trim(),
                ContactRole = request.Contact.ContactRole?.Trim(),
                Phone = request.Contact.Phone?.Trim(),
                Email = request.Contact.Email?.Trim(),
                IsPrimary = request.Contact.IsPrimary,
                CreatedAt = now,
                UpdatedAt = now
            };
        }

        await store.TryCreateBuildingAsync(building, location, contact, ct);
        await store.WriteAuditAsync(command.ActorId, command.OrganizationId, "buildings", building.Id, "Create", now, ct);

        BuildingLocationResponse? locationResponse = location != null ? new BuildingLocationResponse(location.Id, location.Address, location.City, location.District, location.Latitude, location.Longitude, location.Geojson) : null;
        BuildingContactResponse? contactResponse = contact != null ? new BuildingContactResponse(contact.Id, contact.ContactName, contact.ContactRole, contact.Phone, contact.Email, contact.IsPrimary) : null;

        var response = new BuildingResponse(building.Id, building.Name, building.BuildingType, building.TotalFloors, building.IsActive, building.OrganizationId, building.CreatedAt, building.UpdatedAt, locationResponse, contactResponse);
        return AuthResult<BuildingResponse>.Ok(response);
    }
}
