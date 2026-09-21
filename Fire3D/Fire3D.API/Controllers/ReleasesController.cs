using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Route("api/releases")]
[Authorize]
public class ReleasesController(ISender sender) : ControllerBase
{
    /// <summary>
    /// Publishes a release (D17).
    /// </summary>
    [HttpPost("{releaseId:guid}/publish")]
    [ProducesResponseType(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> PublishRelease(Guid releaseId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();

        var result = await sender.Send(new Fire3D.Application.Releases.Commands.PublishRelease.PublishReleaseCommand(actor, releaseId), ct);

        return result.IsSuccess 
            ? Ok() 
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message);
    }
}
