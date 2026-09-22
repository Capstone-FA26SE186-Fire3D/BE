using System.Security.Claims;
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
[Authorize]
public class ScenariosController(ISender sender) : ControllerBase
{
    /// <summary>Validates draft structure. IFC geometry and runtime capability checks require the worker pipeline and are not run by this synchronous endpoint.</summary>
    [HttpPost("/api/scenario-drafts/{draftId:guid}/validate")]
    [ProducesResponseType(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> ValidateScenarioDraft(Guid draftId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();
        var result = await sender.Send(
            new Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft.ValidateScenarioDraftCommand(actor, draftId), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>Loads one immutable scenario snapshot with its complete configuration.</summary>
    [HttpGet("/api/scenario-versions/{versionId:guid}")]
    [ProducesResponseType(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> GetScenarioVersion(Guid versionId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();
        var result = await sender.Send(new GetScenarioVersionQuery(actor, versionId), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>Lists immutable snapshots for a scenario, newest version first.</summary>
    [HttpGet("{scenarioId:guid}/versions")]
    [ProducesResponseType(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> ListScenarioVersions(Guid scenarioId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();
        var result = await sender.Send(new ListScenarioVersionsQuery(actor, scenarioId, page, pageSize), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>Loads a draft state and returns its version as an ETag for the next update.</summary>
    [HttpGet("/api/scenario-drafts/{draftId:guid}")]
    public async Task<IActionResult> GetScenarioDraft(Guid draftId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();
        var result = await sender.Send(new GetScenarioDraftQuery(actor, draftId), ct);
        if (!result.IsSuccess) return Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
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
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();
        var result = await sender.Send(new GetScenarioQuery(actor, scenarioId), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
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
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();
        var result = await sender.Send(new ListBuildingScenariosQuery(actor, buildingId, page, pageSize), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>
    /// Creates a logical scenario associated with a building (D09).
    /// </summary>
    [HttpPost]
    [ProducesResponseType(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> CreateScenario([FromBody] CreateScenarioRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();

        var result = await sender.Send(new CreateScenarioCommand(actor, request), ct);
        
        return result.IsSuccess 
            ? Created($"/api/scenarios/{result.Value}", new { Id = result.Value })
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>
    /// Creates a new draft from an existing scenario (D10).
    /// </summary>
    [HttpPost("{scenarioId:guid}/draft")]
    [ProducesResponseType(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> CreateScenarioDraft(Guid scenarioId, [FromBody] CreateScenarioDraftRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();

        var result = await sender.Send(new CreateScenarioDraftCommand(actor, scenarioId, request), ct);

        return result.IsSuccess 
            ? Created($"/api/scenario-drafts/{result.Value}", new { Id = result.Value })
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
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
    public async Task<IActionResult> UpdateScenarioDraft(Guid draftId, [FromBody] Fire3D.Application.Scenarios.Dto.ScenarioDraftStateDto state, [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();

        if (string.IsNullOrWhiteSpace(ifMatch) || !uint.TryParse(ifMatch.Trim('"'), out var expectedVersion))
        {
            return Problem(statusCode: 412, title: "Precondition Failed", detail: "If-Match header with expected version is required.");
        }

        var result = await sender.Send(new Fire3D.Application.Scenarios.Commands.UpdateScenarioDraft.UpdateScenarioDraftCommand(actor, draftId, expectedVersion, state), ct);

        if (!result.IsSuccess)
        {
            return Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
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
    public async Task<IActionResult> SnapshotScenarioDraft(Guid draftId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();

        var result = await sender.Send(new Fire3D.Application.Scenarios.Commands.SnapshotScenarioDraft.SnapshotScenarioDraftCommand(actor, draftId), ct);

        return result.IsSuccess 
            ? Created($"/api/scenario-versions/{result.Value}", new { Id = result.Value })
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>
    /// Prepares a new VR Playtest Session (D14).
    /// </summary>
    [HttpPost("{scenarioId:guid}/playtests")]
    [ProducesResponseType(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> PreparePlaytestSession(Guid scenarioId, [FromQuery] Guid buildingId, [FromBody] Fire3D.Application.Scenarios.Commands.PreparePlaytestSession.PreparePlaytestRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();

        var result = await sender.Send(new Fire3D.Application.Scenarios.Commands.PreparePlaytestSession.PreparePlaytestSessionCommand(actor, buildingId, scenarioId, request), ct);

        return result.IsSuccess 
            ? Created($"/api/playtests/{result.Value}", new { Id = result.Value })
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>
    /// Starts a prepared VR Playtest Session (D15).
    /// </summary>
    [HttpPost("/api/playtests/{playtestId:guid}/start")]
    [ProducesResponseType(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> StartPlaytestSession(Guid playtestId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();

        var result = await sender.Send(new Fire3D.Application.Scenarios.Commands.StartPlaytestSession.StartPlaytestSessionCommand(actor, playtestId), ct);

        return result.IsSuccess 
            ? Ok()
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }
}

