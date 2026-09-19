using System.Security.Claims;
using Fire3D.Application.Administration;
using Fire3D.Application.Buildings;
using Fire3D.Application.Buildings.Commands.CreateBuilding;
using Fire3D.Application.Buildings.Commands.SetBuildingActive;
using Fire3D.Application.Buildings.Commands.UpdateBuilding;
using Fire3D.Application.Buildings.Queries.GetBuilding;
using Fire3D.Application.Buildings.Queries.ListBuildings;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Route("api/buildings")]
[Authorize] // Ph?i dang nh?p
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class BuildingsController(ISender sender) : ControllerBase
{
    private Guid ActorId => Guid.Parse(User.FindFirstValue("sub")!);
    private Guid OrganizationId => Guid.Parse(User.FindFirstValue("organization_id")!);

    [HttpPost]
    public async Task<ActionResult<BuildingResponse>> CreateBuilding(CreateBuildingRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new CreateBuildingCommand(ActorId, OrganizationId, request), ct);
        return result.IsSuccess ? Created($"/api/buildings/{result.Value!.Id}", result.Value) : Problem();
    }

    [HttpGet]
    public async Task<ActionResult<PageResponse<BuildingSummaryResponse>>> ListBuildings([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null, [FromQuery] bool? isActive = null, CancellationToken ct = default)
    {
        var result = await sender.Send(new ListBuildingsQuery(ActorId, OrganizationId, new BuildingFilter(page, pageSize, search, isActive)), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem();
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BuildingResponse>> GetBuilding(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new GetBuildingQuery(ActorId, OrganizationId, id), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem();
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<BuildingResponse>> UpdateBuilding(Guid id, UpdateBuildingRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new UpdateBuildingCommand(ActorId, OrganizationId, id, request), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem();
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<BuildingSummaryResponse>> DeleteBuilding(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new SetBuildingActiveCommand(ActorId, OrganizationId, id, false), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem();
    }

    [HttpPost("{id:guid}/revisions/upload-url")]
    public async Task<ActionResult> GetUploadUrl(Guid id, CancellationToken ct)
    {
        return StatusCode(501, "S3 Pre-signed URL generation is pending implementation.");
    }

    [HttpGet("{id:guid}/revisions")]
    public async Task<ActionResult<IReadOnlyList<RevisionResponse>>> ListRevisions(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new Fire3D.Application.Buildings.Queries.ListRevisions.ListRevisionsQuery(ActorId, OrganizationId, id), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem();
    }
}
