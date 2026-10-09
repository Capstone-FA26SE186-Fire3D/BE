using Fire3D.API.Authorization;
using Fire3D.Application.Scenarios.Commands.CreateScenario;
using Fire3D.Application.Scenarios.Commands.CreateScenarioDraft;
using Fire3D.Application.Scenarios.Queries.ListBuildingScenarios;
using Fire3D.Application.Scenarios.Queries.GetScenario;
using Fire3D.Application.Scenarios.Queries.GetScenarioDraft;
using Fire3D.Application.Scenarios.Queries.ListScenarioVersions;
using Fire3D.Application.Scenarios.Queries.GetScenarioVersion;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Route("api/scenarios")]
[Authorize(Roles = "OrganizationUser,PlatformAdmin")]
public class ScenariosController(ISender sender, Fire3D.Application.Scenarios.IScenarioReviewQueries reviews) : ControllerBase
{
    /// <summary>Validates draft structure. IFC geometry and runtime capability checks require the worker pipeline and are not run by this synchronous endpoint.</summary>
    [HttpPost("/api/scenario-drafts/{draftId:guid}/validate")]
    [ProducesResponseType(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> ValidateScenarioDraft(Guid draftId, CancellationToken ct)
    {
        var actor = User.GetActorId();
        var result = await sender.Send(
            new Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft.ValidateScenarioDraftCommand(actor, draftId), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    /// <summary>Loads one immutable scenario snapshot with its complete configuration.</summary>
    [HttpGet("/api/scenario-versions/{versionId:guid}")]
    [ProducesResponseType<Fire3D.Application.Scenarios.ScenarioVersionDetailResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> GetScenarioVersion(Guid versionId, CancellationToken ct)
    {
        var actor = User.GetActorId();
        var result = await sender.Send(new GetScenarioVersionQuery(actor, versionId), ct);
        if(result.IsSuccess)
        {
            var states=await reviews.StatesAsync(actor,User.GetSessionFamilyId(),[versionId],ct);
            if(!states.IsSuccess) return ReviewReadProblem(states.Error!);
            var review=states.Value!.SingleOrDefault();
            if(review is null) return Problem(statusCode:404,title:"Scenario version not found.",extensions:new Dictionary<string,object?>{["code"]="NOT_FOUND"});
            return Ok(result.Value! with {ReviewStatus=review.ReviewStatus,ReviewId=review.ReviewId,RejectReason=review.RejectReason});
        }
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    /// <summary>Lists immutable snapshots for a scenario, newest version first.</summary>
    [HttpGet("{scenarioId:guid}/versions")]
    [ProducesResponseType<Fire3D.Application.Administration.PageResponse<Fire3D.Application.Scenarios.ScenarioVersionSummaryResponse>>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> ListScenarioVersions(Guid scenarioId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var actor = User.GetActorId();
        var result = await sender.Send(new ListScenarioVersionsQuery(actor, scenarioId, page, pageSize), ct);
        if(result.IsSuccess)
        {
            var states=await reviews.StatesAsync(actor,User.GetSessionFamilyId(),result.Value!.Items.Select(v=>v.Id).ToArray(),ct);
            if(!states.IsSuccess) return ReviewReadProblem(states.Error!);
            var byId=states.Value!.ToDictionary(v=>v.ScenarioVersionId);
            var items=result.Value.Items.Where(v=>byId.ContainsKey(v.Id)).Select(v=>v with {ReviewStatus=byId[v.Id].ReviewStatus,ReviewId=byId[v.Id].ReviewId,RejectReason=byId[v.Id].RejectReason}).ToArray();
            return Ok(result.Value with {Items=items});
        }
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    private ObjectResult ReviewReadProblem(Fire3D.Application.Authentication.AuthError error)=>Problem(statusCode:error.Status,title:error.Message,
        extensions:new Dictionary<string,object?>{["code"]=error.Code,["errors"]=error.Errors});

    /// <summary>Loads a draft state and returns its version as an ETag for the next update.</summary>
    [HttpGet("/api/scenario-drafts/{draftId:guid}")]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> GetScenarioDraft(Guid draftId, CancellationToken ct)
    {
        var actor = User.GetActorId();
        var result = await sender.Send(new GetScenarioDraftQuery(actor, draftId), ct);
        if (!result.IsSuccess) return Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
        Response.Headers.ETag = $"\"{result.Value!.Version}\"";
        return Ok(result.Value);
    }

    /// <summary>Returns authoring metadata for one scenario in the caller's organization scope.</summary>
    [HttpGet("{scenarioId:guid}")]
    [ProducesResponseType(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> GetScenario(Guid scenarioId, CancellationToken ct)
    {
        var actor = User.GetActorId();
        var result = await sender.Send(new GetScenarioQuery(actor, scenarioId), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    /// <summary>Lists scenarios that belong to a building in the caller's organization scope.</summary>
    [HttpGet("/api/buildings/{buildingId:guid}/scenarios")]
    [ProducesResponseType(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> ListBuildingScenarios(Guid buildingId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var actor = User.GetActorId();
        var result = await sender.Send(new ListBuildingScenariosQuery(actor, buildingId, page, pageSize), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    /// <summary>
    /// Creates a logical scenario associated with a building (D09).
    /// </summary>
    [HttpPost]
    [ProducesResponseType(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> CreateScenario([FromBody] CreateScenarioRequest request, CancellationToken ct, [FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader] string? key = null)
    {
        var actor = User.GetActorId();

        var result = await sender.Send(new CreateScenarioCommand(actor, request, key), ct);
        
        return result.IsSuccess 
            ? Created($"/api/scenarios/{result.Value}", new { Id = result.Value })
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    /// <summary>
    /// Creates a new draft from an existing scenario (D10).
    /// </summary>
    [HttpPost("{scenarioId:guid}/draft")]
    [ProducesResponseType(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> CreateScenarioDraft(Guid scenarioId, [FromBody] CreateScenarioDraftRequest request, CancellationToken ct, [FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader] string? key = null)
    {
        var actor = User.GetActorId();

        var result = await sender.Send(new CreateScenarioDraftCommand(actor, scenarioId, request, key), ct);

        return result.IsSuccess 
            ? Created($"/api/scenario-drafts/{result.Value}", new { Id = result.Value })
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    /// <summary>
    /// Updates the state of a scenario draft (D11). Requires If-Match header for concurrency control.
    /// </summary>
    [HttpPut("/api/scenario-drafts/{draftId:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<IActionResult> UpdateScenarioDraft(Guid draftId, [FromBody] Fire3D.Application.Scenarios.Dto.ScenarioDraftStateDto state, [FromHeader(Name="If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader] string? ifMatch, CancellationToken ct)
    {
        var actor = User.GetActorId();

        var precondition = DraftPrecondition(ifMatch, out var expectedVersion);
        if (precondition is not null) return precondition;

        var result = await sender.Send(new Fire3D.Application.Scenarios.Commands.UpdateScenarioDraft.UpdateScenarioDraftCommand(actor, draftId, expectedVersion, state), ct);

        if (!result.IsSuccess)
        {
            return Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
        }

        Response.Headers["ETag"] = $"\"{result.Value}\"";
        return NoContent();
    }

    /// <summary>
    /// Creates an immutable snapshot (ScenarioVersion) from a draft (D12).
    /// </summary>
    [HttpPost("/api/scenario-drafts/{draftId:guid}/snapshot")]
    [ProducesResponseType(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> SnapshotScenarioDraft(Guid draftId, CancellationToken ct, [FromHeader(Name="If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader] string? ifMatch = null, [FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader] string? key = null)
    {
        var actor = User.GetActorId();

        var precondition = DraftPrecondition(ifMatch, out var expectedVersion);
        if (precondition is not null) return precondition;
        var result = await sender.Send(new Fire3D.Application.Scenarios.Commands.SnapshotScenarioDraft.SnapshotScenarioDraftCommand(actor, draftId, expectedVersion, key), ct);

        return result.IsSuccess 
            ? Created($"/api/scenario-versions/{result.Value}", new { Id = result.Value })
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    /// <summary>
    /// Pins an accepted immutable PlaytestPackage without consuming Trial or issuing a grant. Idempotency-Key required.
    /// </summary>
    [Authorize(Roles="OrganizationUser")]
    [HttpPost("{scenarioId:guid}/playtests")]
    [ProducesResponseType<Fire3D.Application.Scenarios.PlaytestPreparation>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> PreparePlaytestSession(Guid scenarioId, [FromQuery] Guid? buildingId, [FromBody] Fire3D.Application.Scenarios.Commands.PreparePlaytestSession.PreparePlaytestRequest request, [FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var actor = User.GetActorId();

        var result = await sender.Send(new Fire3D.Application.Scenarios.Commands.PreparePlaytestSession.PreparePlaytestSessionCommand(actor, User.GetSessionFamilyId(), buildingId, scenarioId, request, key), ct);

        return result.IsSuccess 
            ? Created($"/api/playtests/{result.Value!.Id}", result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    /// <summary>
    /// OrganizationUser owner starts with an active session, compatible runtime and Building entitlement. Atomic Trial consumption; returns a dedicated five-minute launch grant.
    /// </summary>
    [Authorize(Roles="OrganizationUser")]
    [HttpPost("/api/playtests/{playtestId:guid}/start")]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(503)]
    [ProducesResponseType<Fire3D.Application.Scenarios.PlaytestLaunch>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> StartPlaytestSession(Guid playtestId, [FromBody] Fire3D.Application.Scenarios.StartPlaytestRequest request, [FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader] string? key, CancellationToken ct)
    {
        var actor = User.GetActorId();

        var result = await sender.Send(new Fire3D.Application.Scenarios.Commands.StartPlaytestSession.StartPlaytestSessionCommand(actor, User.GetSessionFamilyId(), playtestId, request, key), ct);

        return result.IsSuccess 
            ? Ok(result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }
    private IActionResult? DraftPrecondition(string? value, out uint revision)
    {
        revision = 0;
        if (string.IsNullOrWhiteSpace(value)) return Problem(statusCode:428, title:"If-Match is required.", extensions:new Dictionary<string,object?>{["code"]="PRECONDITION_REQUIRED"});
        if (value.Length < 3 || value[0]!='"' || value[^1]!='"' || !uint.TryParse(value.AsSpan(1,value.Length-2),System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out revision) || revision==0)
            return Problem(statusCode:400,title:"If-Match must be one quoted draft revision.", extensions:new Dictionary<string,object?>{["code"]="INVALID_IF_MATCH"});
        return null;
    }

}

