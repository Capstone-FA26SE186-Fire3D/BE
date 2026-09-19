using System.Security.Claims;
using Fire3D.Application.Buildings;
using Fire3D.Application.Buildings.Queries.GetRevision;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

[ApiController]
[Route("api/revisions")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class RevisionsController(ISender sender) : ControllerBase
{
    private Guid ActorId => Guid.Parse(User.FindFirstValue("sub")!);
    private Guid OrganizationId => Guid.Parse(User.FindFirstValue("organization_id")!);

    /// <summary>
    /// Lấy chi tiết thông tin một Revision (bản vẽ) cụ thể
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RevisionResponse>> GetRevision(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new GetRevisionQuery(ActorId, OrganizationId, id), ct);
        return result.IsSuccess
            ? Ok(result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }
}
