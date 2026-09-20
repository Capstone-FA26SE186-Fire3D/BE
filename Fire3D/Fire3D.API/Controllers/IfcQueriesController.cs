using System.Security.Claims;
using Fire3D.Application.Administration;
using Fire3D.Application.Ifc;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Fire3D.API.Controllers;
[ApiController]
[Authorize]
[Route("api")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class IfcQueriesController(ISender sender) : ControllerBase
{
    /// <summary>Liệt kê logical processing jobs của revision IFC.</summary>
    /// <remarks>
    /// Bearer token của OrganizationUser đúng tenant hoặc PlatformAdmin. Trainee bị từ chối.
    /// page mặc định 1, pageSize 20, tối đa 100. Sắp xếp createdAt giảm dần, id làm khóa phụ.
    /// Trả items,totalCount,page,pageSize; mỗi item gồm id,revisionId,sourceDocumentId,scenarioVersionId,
    /// kind,status,createdAt. Revision chưa có job trả items rỗng; revision không tồn tại/ngoài scope trả 404.
    /// Đây là trạng thái đã lưu trong PostgreSQL, không phải tiến độ ước lượng từ notification.
    /// </remarks>
    [HttpGet("revisions/{revisionId:guid}/processing-jobs")]
    [ProducesResponseType<PageResponse<ProcessingJobResponse>>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<ActionResult<PageResponse<ProcessingJobResponse>>> ListJobs(Guid revisionId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();
        var result = await sender.Send(new ListProcessingJobsQuery(actor, revisionId, page, pageSize), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status,
            title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }
}
