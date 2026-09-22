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
}
