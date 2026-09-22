using Fire3D.API.Authorization;
using Fire3D.Application.Administration;
using Fire3D.Application.Administration.Queries.GetMyOrganization;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Route("api/organizations/me")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OrganizationProfileController(ISender sender) : ControllerBase
{
    /// <summary>Returns the current OrganizationUser's organization without exposing the platform organization list.</summary>
    [HttpGet]
    [ProducesResponseType<OrganizationResponse>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    public async Task<ActionResult<OrganizationResponse>> Get(CancellationToken ct)
    {
        var result = await sender.Send(new GetMyOrganizationQuery(User.GetActorId()), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }
}
