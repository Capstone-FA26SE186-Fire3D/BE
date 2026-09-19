using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Buildings.Queries.GetBuilding;

internal sealed class GetBuildingQueryHandler(IBuildingStore store)
    : IRequestHandler<GetBuildingQuery, AuthResult<BuildingResponse>>
{
    public async Task<AuthResult<BuildingResponse>> Handle(GetBuildingQuery query, CancellationToken ct)
    {
        var building = await store.FindBuildingAsync(query.BuildingId, query.OrganizationId, ct);
        if (building == null)
            return AuthResult<BuildingResponse>.Fail("NOT_FOUND", "Building not found.", 404);

        BuildingLocationResponse? locationResponse = building.BuildingLocation != null 
            ? new BuildingLocationResponse(building.BuildingLocation.Id, building.BuildingLocation.Address, building.BuildingLocation.City, building.BuildingLocation.District, building.BuildingLocation.Latitude, building.BuildingLocation.Longitude, building.BuildingLocation.Geojson) 
            : null;
            
        BuildingContactResponse? contactResponse = building.BuildingContact != null 
            ? new BuildingContactResponse(building.BuildingContact.Id, building.BuildingContact.ContactName, building.BuildingContact.ContactRole, building.BuildingContact.Phone, building.BuildingContact.Email, building.BuildingContact.IsPrimary) 
            : null;

        var response = new BuildingResponse(building.Id, building.Name, building.BuildingType, building.TotalFloors, building.IsActive, building.OrganizationId, building.CreatedAt, building.UpdatedAt, locationResponse, contactResponse);
        return AuthResult<BuildingResponse>.Ok(response);
    }
}
