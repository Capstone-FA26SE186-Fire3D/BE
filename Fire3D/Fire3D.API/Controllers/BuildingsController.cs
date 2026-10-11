using Fire3D.API.Authorization;
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
    private Guid ActorId => User.GetActorId();
    private Guid? OrganizationId => User.GetOrganizationId();

    /// <summary>Creates a Building in a live organization with atomic audit.</summary>
    /// <remarks>OrganizationUser uses their database tenant. PlatformAdmin must select organizationId in the body.
    /// The organizationId query parameter is a deprecated alias; conflicting body/query values return 400.
    /// Validation returns code VALIDATION_ERROR with errors keyed by field.</remarks>
    [HttpPost]
    [ProducesResponseType<BuildingResponse>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(409)]
    [Authorize(Roles = "PlatformAdmin,OrganizationUser")]
    public async Task<ActionResult<BuildingResponse>> CreateBuilding(CreateBuildingRequest request, [FromQuery] Guid? organizationId, CancellationToken ct)
    {
        var result = await sender.Send(new CreateBuildingCommand(ActorId, organizationId, request), ct);
        return result.IsSuccess ? Created($"/api/buildings/{result.Value!.Id}", result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    [HttpGet]
    [Authorize(Roles = "PlatformAdmin,OrganizationUser")]
    public async Task<ActionResult<PageResponse<BuildingSummaryResponse>>> ListBuildings([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null, [FromQuery] bool? isActive = null, [FromQuery] Guid? organizationId = null, CancellationToken ct = default)
    {
        var result = await sender.Send(new ListBuildingsQuery(ActorId, organizationId, new BuildingFilter(page, pageSize, search, isActive)), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    [HttpGet("{id:guid}")]
    [Authorize(Roles = "PlatformAdmin,OrganizationUser")]
    public async Task<ActionResult<BuildingResponse>> GetBuilding(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new GetBuildingQuery(ActorId, null, id), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "PlatformAdmin,OrganizationUser")]
    public async Task<ActionResult<BuildingResponse>> UpdateBuilding(Guid id, UpdateBuildingRequest request, [FromQuery] Guid? organizationId, CancellationToken ct)
    {
        var result = await sender.Send(new UpdateBuildingCommand(ActorId, organizationId, id, request), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "PlatformAdmin,OrganizationUser")]
    public async Task<ActionResult<BuildingSummaryResponse>> DeleteBuilding(Guid id, [FromQuery] Guid? organizationId, CancellationToken ct)
    {
        var result = await sender.Send(new SetBuildingActiveCommand(ActorId, organizationId, id, false), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    [Authorize(Roles="OrganizationUser,PlatformAdmin")]
    [HttpPost("{id:guid}/revisions/upload-url")]
    [ProducesResponseType<Fire3D.Application.Ifc.Commands.InitiateUpload.InitiateIfcUploadResponse>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<ActionResult<Fire3D.Application.Ifc.Commands.InitiateUpload.InitiateIfcUploadResponse>> GetUploadUrl(
        Guid id, Fire3D.Application.Ifc.Commands.InitiateUpload.InitiateIfcUploadRequest request,
        [FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var result = await sender.Send(new Fire3D.Application.Ifc.Commands.InitiateUpload.InitiateIfcUploadCommand(
            User.GetActorId(), id, request, key), ct);
        return result.IsSuccess
            ? Created($"/api/revisions/{result.Value!.RevisionId}", result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
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
        var actorId = User.GetActorId();
        var result = await sender.Send(new Fire3D.Application.Buildings.Queries.ListRevisions.ListRevisionsQuery(
            actorId, id, page, pageSize), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status,
            title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    /// <summary>
    /// Gets a list of Published trainings for a given building (D18).
    /// </summary>
    [HttpGet("{id:guid}/trainings")]
    [Authorize(Roles="Trainee,OrganizationUser,PlatformAdmin")]
    [ProducesResponseType<List<Fire3D.Application.Buildings.Queries.GetTrainings.TrainingDto>>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> GetTrainings(Guid id, CancellationToken ct)
    {
        var actor = User.GetActorId();

        var result = await sender.Send(new Fire3D.Application.Buildings.Queries.GetTrainings.GetTrainingsQuery(actor, id,User.GetSessionFamilyId()), ct);

        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }
}
