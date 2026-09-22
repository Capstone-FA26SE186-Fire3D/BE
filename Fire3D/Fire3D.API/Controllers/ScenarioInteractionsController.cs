using Fire3D.API.Authorization;
using Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Route("api/scenario-interactions")]
[Authorize]
public class ScenarioInteractionsController(ISender sender) : ControllerBase
{
    /// <summary>
    /// Returns the catalog of supported VR/Runtime capabilities (D13).
    /// </summary>
    [HttpGet("catalog")]
    [ProducesResponseType(typeof(List<RuntimeCatalogDto>), 200)]
    public async Task<IActionResult> GetCatalog(CancellationToken ct)
    {
        var actor = User.GetActorId();

        var result = await sender.Send(new GetRuntimeCatalogQuery(actor), ct);

        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message);
    }
}
