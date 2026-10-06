using Fire3D.Application.Authentication;
using Fire3D.Domain.Entities;
using MediatR;

namespace Fire3D.Application.Buildings.Commands.CreateBuilding;

internal sealed class CreateBuildingCommandHandler(IBuildingStore store, IAuthStore accounts, TimeProvider clock)
    : IRequestHandler<CreateBuildingCommand, AuthResult<BuildingResponse>>
{
    public async Task<AuthResult<BuildingResponse>> Handle(CreateBuildingCommand command, CancellationToken ct)
    {
        var request = command.Request;
        var name = request.Name?.Trim();
        
        var errors = BuildingValidation.Validate(name, request.BuildingType, request.TotalFloors, request.Location, request.Contact);
        var actor = await accounts.FindUserAsync(command.ActorId, ct);
        var alias = command.OrganizationId == Guid.Empty ? null : command.OrganizationId;
        if (request.OrganizationId == Guid.Empty || (request.OrganizationId.HasValue && alias.HasValue && request.OrganizationId != alias))
            errors["organizationId"] = ["Body and query organizationId must identify the same non-empty organization."];
        var organizationId = request.OrganizationId ?? alias ?? actor?.OrganizationId;
        if (actor?.Role == Fire3D.Domain.Enums.UserRole.PlatformAdmin && !organizationId.HasValue)
            errors["organizationId"] = ["PlatformAdmin must select organizationId in the request body."];
        if (errors.Count > 0)
            return AuthResult<BuildingResponse>.Fail("VALIDATION_ERROR", "Building validation failed.", 400, errors);
        if (!organizationId.HasValue || !await BuildingAuthorization.CanMutateAsync(accounts, command.ActorId, organizationId.Value, ct))
            return AuthResult<BuildingResponse>.Fail("FORBIDDEN", "An active organization scope is required.", 403);
        var now = clock.GetUtcNow().UtcDateTime;
        var buildingId = Guid.NewGuid();
        
        var building = new Building
        {
            Id = buildingId,
            OrganizationId = organizationId.Value,
            Name = name!,
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
                ContactName = request.Contact.ContactName?.Trim() ?? string.Empty,
                ContactRole = request.Contact.ContactRole?.Trim(),
                Phone = request.Contact.Phone?.Trim(),
                Email = request.Contact.Email?.Trim(),
                IsPrimary = request.Contact.IsPrimary,
                CreatedAt = now,
                UpdatedAt = now
            };
        }

        if (!await store.CreateBuildingWithAuditAsync(building, location, contact, command.ActorId, now, ct))
            return AuthResult<BuildingResponse>.Fail("BUILDING_MUTATION_FAILED", "Building could not be created.", 409);

        BuildingLocationResponse? locationResponse = location != null ? new BuildingLocationResponse(location.Id, location.Address, location.City, location.District, location.Latitude, location.Longitude, location.Geojson) : null;
        BuildingContactResponse? contactResponse = contact != null ? new BuildingContactResponse(contact.Id, contact.ContactName, contact.ContactRole, contact.Phone, contact.Email, contact.IsPrimary) : null;

        var response = new BuildingResponse(building.Id, building.Name, building.BuildingType, building.TotalFloors, building.IsActive, building.OrganizationId, building.CreatedAt, building.UpdatedAt, locationResponse, contactResponse);
        return AuthResult<BuildingResponse>.Ok(response);
    }
}
