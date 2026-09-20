using System.Security.Claims;
using Fire3D.Application.Scenarios.Commands.CreateScenario;
using Fire3D.Application.Scenarios.Commands.CreateScenarioDraft;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Route("api/scenarios")]
[Authorize]
public class ScenariosController(ISender sender) : ControllerBase
{
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
}
