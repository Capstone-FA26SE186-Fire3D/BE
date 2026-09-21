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
    private Guid ActorId => Guid.TryParse(User.FindFirstValue("sub"), out var sub) ? sub : Guid.Empty;
    private Guid? OrganizationId => Guid.TryParse(User.FindFirstValue("organization_id"), out var orgId) ? orgId : null;

    [HttpPost]
    public async Task<ActionResult<BuildingResponse>> CreateBuilding(CreateBuildingRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new CreateBuildingCommand(ActorId, (OrganizationId ?? Guid.Empty), request), ct);
        return result.IsSuccess ? Created($"/api/buildings/{result.Value!.Id}", result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message);
    }

    [HttpGet]
    public async Task<ActionResult<PageResponse<BuildingSummaryResponse>>> ListBuildings([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null, [FromQuery] bool? isActive = null, CancellationToken ct = default)
    {
        var result = await sender.Send(new ListBuildingsQuery(ActorId, (OrganizationId ?? Guid.Empty), new BuildingFilter(page, pageSize, search, isActive)), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BuildingResponse>> GetBuilding(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new GetBuildingQuery(ActorId, (OrganizationId ?? Guid.Empty), id), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<BuildingResponse>> UpdateBuilding(Guid id, UpdateBuildingRequest request, CancellationToken ct)
    {
        var result = await sender.Send(new UpdateBuildingCommand(ActorId, (OrganizationId ?? Guid.Empty), id, request), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message);
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<BuildingSummaryResponse>> DeleteBuilding(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new SetBuildingActiveCommand(ActorId, (OrganizationId ?? Guid.Empty), id, false), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message);
    }

    [HttpPost("{id:guid}/revisions/upload-url")]
    public async Task<ActionResult> GetUploadUrl(Guid id, CancellationToken ct)
    {
        return StatusCode(501, "S3 Pre-signed URL generation is pending implementation.");
    }

    /// <summary>Danh sách revision IFC của Building, có phân trang.</summary>
    /// <remarks>
    /// OrganizationUser chỉ xem tổ chức của mình; PlatformAdmin được xem Building đích.
    /// Role/tenant lấy từ DB; Trainee bị từ chối. page mặc định 1; pageSize mặc định 20, tối đa 100.
    /// Response gồm items,totalCount,page,pageSize. Building hợp lệ chưa có revision trả items rỗng.
    /// Building không tồn tại, bị archive hoặc khác tenant trả 404. Không trả raw IFC/storage key.
    /// </remarks>
    [HttpGet("{id:guid}/revisions")]
    [ProducesResponseType<PageResponse<RevisionResponse>>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<ActionResult<PageResponse<RevisionResponse>>> ListRevisions(
        Guid id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actorId)) return Unauthorized();
        var result = await sender.Send(new Fire3D.Application.Buildings.Queries.ListRevisions.ListRevisionsQuery(
            actorId, id, page, pageSize), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status,
            title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>
    /// Gets a list of Published trainings for a given building (D18).
    /// </summary>
    [HttpGet("{id:guid}/trainings")]
    [ProducesResponseType<List<Fire3D.Application.Buildings.Queries.GetTrainings.TrainingDto>>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> GetTrainings(Guid id, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();

        var result = await sender.Send(new Fire3D.Application.Buildings.Queries.GetTrainings.GetTrainingsQuery(actor, id), ct);

        return result.IsSuccess 
            ? Ok(result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message);
    }
}
