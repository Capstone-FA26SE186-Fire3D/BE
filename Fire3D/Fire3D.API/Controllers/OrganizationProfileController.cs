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
[Authorize(Roles = "OrganizationUser")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class OrganizationProfileController(ISender sender) : ControllerBase
{
    /// <summary>Returns the current OrganizationUser's organization without exposing the platform organization list.</summary>
    [HttpGet]
    [ProducesResponseType<OrganizationProfileResponse>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<OrganizationProfileResponse>> Get(CancellationToken ct)
    {
        var result = await sender.Send(new GetMyOrganizationQuery(User.GetActorId()), ct);
        if (!result.IsSuccess) return Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
        Response.Headers.ETag = ProfileEtag.Format(result.Value!.ProfileRevision);
        return Ok(result.Value);
    }

    /// <summary>Updates the current organization's name, address or phone with If-Match.</summary>
    /// <remarks>Omitted fields are unchanged; explicit null/empty is invalid. Keeping the current phone is valid.
    /// Phone reserved by another organization (including inactive/deleted) returns 409 ORGANIZATION_PHONE_EXISTS
    /// with errors.phoneNumber; revision/audit remain unchanged. Missing If-Match returns 428,
    /// malformed 400, stale 412. Canonical normalization does not infer country codes.</remarks>
    [HttpPatch]
    [ProducesResponseType<OrganizationProfileResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(412)]
    [ProducesResponseType<ProblemDetails>(428)]
    [Fire3D.API.OpenApi.ResponseHeader("ETag")]
    public async Task<ActionResult<OrganizationProfileResponse>> Update([FromBody] UpdateOrganizationProfileRequest request,
        [FromHeader(Name="If-Match"), Fire3D.API.OpenApi.RequiredRequestHeader] string? ifMatch, CancellationToken ct)
    {
        var result = await sender.Send(new UpdateMyOrganizationCommand(User.GetActorId(), ifMatch, request), ct);
        if (!result.IsSuccess) return Problem(statusCode: result.Error!.Status, title: result.Error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
        Response.Headers.ETag = ProfileEtag.Format(result.Value!.ProfileRevision);
        return Ok(result.Value);
    }
}
