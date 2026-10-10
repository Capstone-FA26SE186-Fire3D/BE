using Fire3D.API.Authorization;
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
    /// <summary>Đọc metadata revision IFC thuộc phạm vi tổ chức được phép.</summary>
    /// <remarks>
    /// Cần Bearer token hợp lệ. OrganizationUser chỉ xem revision của tổ chức mình;
    /// PlatformAdmin được xem tổ chức đích bằng revision ID. Trainee bị từ chối.
    /// Role và tenant được kiểm tra lại từ database. Không trả object key, signed URL hoặc raw IFC.
    /// Không tìm thấy hoặc khác tenant trả 404; tài khoản/tổ chức bị khóa trả 401.
    /// </remarks>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<RevisionResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<ActionResult<RevisionResponse>> GetRevision(Guid id, CancellationToken ct)
    {
        var actorId = User.GetActorId();
        var result = await sender.Send(new GetRevisionQuery(actorId, id), ct);
        return result.IsSuccess ? Ok(result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    /// <summary>Lists floors of the accepted versioned Geometry artifact for one revision.</summary>
    /// <remarks>OrganizationUser in the tenant or PlatformAdmin. Status Ready returns floors with stable IDs, elevation in
    /// metres, optional IFC GlobalId and a column-major floor→GLB transform (fet3d.editor/1). NotReady means no accepted
    /// Geometry artifact; ReprocessRequired means the artifact predates or fails the editor contract and no coordinates
    /// are returned. Missing or other-tenant revision returns 404.</remarks>
    [HttpGet("{id:guid}/floors")]
    [ProducesResponseType<Fire3D.Application.Ifc.RevisionFloorsResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> GetFloors(Guid id, CancellationToken ct)
    {
        var result = await sender.Send(new Fire3D.Application.Ifc.GetRevisionFloorsQuery(User.GetActorId(), id), ct);
        return result.IsSuccess ? Ok(result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }
}
