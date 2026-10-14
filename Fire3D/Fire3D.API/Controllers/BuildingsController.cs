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
    /// Validation returns code VALIDATION_ERROR with errors keyed by field.
    /// Idempotency-Key (1–128 printable characters, no spaces) is required: missing or invalid returns 400 IDEMPOTENCY_KEY_REQUIRED.
    /// Retrying with the same actor, key and input returns 201 with the Building the first request created (current state, same id),
    /// without another Building or audit. The same key with different input returns 409 IDEMPOTENCY_KEY_CONFLICT.
    /// The response carries the Building ETag for a later PUT/DELETE.</remarks>
    [HttpPost]
    [ProducesResponseType<BuildingResponse>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(409)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    [Authorize(Roles = "PlatformAdmin,OrganizationUser")]
    public async Task<ActionResult<BuildingResponse>> CreateBuilding(CreateBuildingRequest request, [FromQuery, Fire3D.API.OpenApi.DeprecatedParameter("Deprecated alias; send organizationId in the body. A different value in query and body returns 400.")] Guid? organizationId,
        [FromHeader(Name = "Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader] string? idempotencyKey, CancellationToken ct)
    {
        var result = await sender.Send(new CreateBuildingCommand(ActorId, organizationId, request, idempotencyKey), ct);
        if (!result.IsSuccess) return Failure(result.Error!);
        Response.Headers.ETag = BuildingEtag.Format(result.Value!.UpdatedAt);
        return Created($"/api/buildings/{result.Value.Id}", result.Value);
    }

    [HttpGet]
    [Authorize(Roles = "PlatformAdmin,OrganizationUser")]
    public async Task<ActionResult<PageResponse<BuildingSummaryResponse>>> ListBuildings([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null, [FromQuery] bool? isActive = null, [FromQuery] Guid? organizationId = null, CancellationToken ct = default)
    {
        var result = await sender.Send(new ListBuildingsQuery(ActorId, organizationId, new BuildingFilter(page, pageSize, search, isActive)), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    /// <summary>Reads one Building and its ETag.</summary>
    /// <remarks>The ETag is the quoted Building version; send it in If-Match on PUT/DELETE. Archived Buildings stay readable.</remarks>
    [HttpGet("{id:guid}")]
    [Authorize(Roles = "PlatformAdmin,OrganizationUser")]
    [ProducesResponseType<BuildingResponse>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<BuildingResponse>> GetBuilding(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new GetBuildingQuery(ActorId, null, id), ct);
        if (!result.IsSuccess) return Failure(result.Error!);
        Response.Headers.ETag = BuildingEtag.Format(result.Value!.UpdatedAt);
        return Ok(result.Value);
    }

    /// <summary>Replaces name, type, floors and optional location/contact of an active Building with If-Match.</summary>
    /// <remarks>Missing If-Match returns 428 PRECONDITION_REQUIRED; malformed 400 VALIDATION_ERROR. When the Building changed
    /// since the ETag was read (another user, PlatformAdmin, archive), 412 PRECONDITION_FAILED carries the current Building in
    /// current and its ETag header, and nothing is written. Location/contact null keep stored data. Archived: 404.</remarks>
    [HttpPut("{id:guid}")]
    [Authorize(Roles = "PlatformAdmin,OrganizationUser")]
    [ProducesResponseType<BuildingResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<BuildingResponse>> UpdateBuilding(Guid id, UpdateBuildingRequest request, [FromQuery] Guid? organizationId,
        [FromHeader(Name = "If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader] string? ifMatch, CancellationToken ct)
    {
        var result = await sender.Send(new UpdateBuildingCommand(ActorId, organizationId, id, request, ifMatch), ct);
        if (!result.IsSuccess) return await FailureWithCurrent(id, result.Error!, ct);
        Response.Headers.ETag = BuildingEtag.Format(result.Value!.UpdatedAt);
        return Ok(result.Value);
    }

    /// <summary>Archives a Building (isActive=false) with If-Match; history is kept.</summary>
    /// <remarks>Missing If-Match returns 428; malformed 400; stale 412 with the current Building in current. A Building that is
    /// already archived returns 200 without a write or audit, so a retried DELETE succeeds.</remarks>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "PlatformAdmin,OrganizationUser")]
    [ProducesResponseType<BuildingSummaryResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<BuildingSummaryResponse>> DeleteBuilding(Guid id, [FromQuery] Guid? organizationId,
        [FromHeader(Name = "If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader] string? ifMatch, CancellationToken ct)
    {
        var result = await sender.Send(new SetBuildingActiveCommand(ActorId, organizationId, id, false, ifMatch), ct);
        if (!result.IsSuccess) return await FailureWithCurrent(id, result.Error!, ct);
        Response.Headers.ETag = BuildingEtag.Format(result.Value!.UpdatedAt);
        return Ok(result.Value);
    }

    private ObjectResult Failure(Fire3D.Application.Authentication.AuthError error, object? current = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code, ["errors"] = error.Errors };
        if (current is not null) extensions["current"] = current;
        return Problem(statusCode: error.Status, title: error.Message, extensions: extensions);
    }

    // 412 lets the client compare with the stored Building before retrying with the fresh ETag.
    private async Task<ObjectResult> FailureWithCurrent(Guid id, Fire3D.Application.Authentication.AuthError error, CancellationToken ct)
    {
        if (error.Status != 412) return Failure(error);
        var current = await sender.Send(new GetBuildingQuery(ActorId, null, id), ct);
        if (!current.IsSuccess) return Failure(error);
        Response.Headers.ETag = BuildingEtag.Format(current.Value!.UpdatedAt);
        return Failure(error, current.Value);
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
