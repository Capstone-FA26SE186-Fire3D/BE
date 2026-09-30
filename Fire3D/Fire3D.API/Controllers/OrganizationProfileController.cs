using Fire3D.API.Authorization;
using Fire3D.Application.Administration;
using Fire3D.Application.Administration.Queries.GetMyOrganization;
using Fire3D.Application.Administration.Commands.UpdateMyOrganization;
using Fire3D.Application.Authentication.Commands.RegisterUser;
using Fire3D.Application.Authentication.Internal;
using Fire3D.Application.Authentication;
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
    [ProducesResponseType<OrganizationProfileResponse>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    public async Task<ActionResult<OrganizationProfileResponse>> Get(CancellationToken ct)
    {
        var result = await sender.Send(new GetMyOrganizationQuery(User.GetActorId()), ct);
        if (!result.IsSuccess) return Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
        Response.Headers.ETag = ProfileEtag.Format(result.Value!.ProfileRevision);
        return Ok(result.Value);
    }

    [HttpPatch]
    [ProducesResponseType<OrganizationProfileResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    public async Task<ActionResult<OrganizationProfileResponse>> Update([FromBody] UpdateOrganizationProfileRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct)
    {
        var result = await sender.Send(new UpdateMyOrganizationCommand(User.GetActorId(), ifMatch, request), ct);
        if (!result.IsSuccess) return Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
        Response.Headers.ETag = ProfileEtag.Format(result.Value!.ProfileRevision);
        return Ok(result.Value);
    }
}
