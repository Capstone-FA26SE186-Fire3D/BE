using System.Security.Claims;
using Fire3D.Application.Ifc;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
namespace Fire3D.API.Controllers;
[ApiController]
[Authorize]
[EnableRateLimiting("administration")]
[Route("api")]
[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class IfcCommandsController(ISender sender) : ControllerBase
{
    /// <summary>Requeue một processing job Failed qua gate database và transactional outbox.</summary>
    /// <remarks>
    /// OrganizationUser đúng tenant hoặc PlatformAdmin. Body: requestId (UUID mới cho một ý định retry),
    /// reason (1-1000 ký tự). Gửi lại đúng requestId/reason trả AlreadyRequeued, kể cả job đã chạy xong sau retry.
    /// Cùng key khác job/reason trả 409; key mới chỉ dùng với Failed. Cancelled/Succeeded không tự chạy lại.
    /// Response 202 có jobId/outcome và Location theo dõi job. 202 xác nhận giao việc bền vững, chưa xác nhận worker hoàn tất.
    /// Cần gate requeue_processing_job, outbox và quyền DB theo v6.7; không ghi trực tiếp status để bỏ qua gate.
    /// </remarks>
    [HttpPost("processing-jobs/{jobId:guid}/retry")]
    [ProducesResponseType<RetryProcessingJobResponse>(202)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<ActionResult<RetryProcessingJobResponse>> RetryJob(Guid jobId,
        RetryProcessingJobRequest request,CancellationToken ct)
    {
        if(!Guid.TryParse(User.FindFirstValue("sub"),out var actor)) return Unauthorized();
        var result=await sender.Send(new RetryProcessingJobCommand(actor,jobId,request),ct);
        return result.IsSuccess ? Accepted($"/api/processing-jobs/{jobId}",result.Value)
            : Problem(statusCode:result.Error!.Status,title:result.Error.Message,
                extensions:new Dictionary<string,object?> {["code"]=result.Error.Code});
    }

    [HttpPost("buildings/{buildingId:guid}/ifc")]
    [ProducesResponseType<Fire3D.Application.Ifc.Commands.InitiateUpload.InitiateIfcUploadResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<ActionResult<Fire3D.Application.Ifc.Commands.InitiateUpload.InitiateIfcUploadResponse>> InitiateUpload(Guid buildingId,
        Fire3D.Application.Ifc.Commands.InitiateUpload.InitiateIfcUploadRequest request, CancellationToken ct)
    {
        if(!Guid.TryParse(User.FindFirstValue("sub"),out var actor)) return Unauthorized();
        var result = await sender.Send(new Fire3D.Application.Ifc.Commands.InitiateUpload.InitiateIfcUploadCommand(actor, buildingId, request), ct);
        return result.IsSuccess ? Ok(result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    [HttpPost("revisions/{revisionId:guid}/upload-complete")]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> FinalizeUpload(Guid revisionId,
        Fire3D.Application.Ifc.Commands.FinalizeUpload.FinalizeIfcUploadRequest request, CancellationToken ct)
    {
        if(!Guid.TryParse(User.FindFirstValue("sub"),out var actor)) return Unauthorized();
        var result = await sender.Send(new Fire3D.Application.Ifc.Commands.FinalizeUpload.FinalizeIfcUploadCommand(actor, revisionId, request), ct);
        return result.IsSuccess ? NoContent()
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }

    [HttpPost("revisions/{revisionId:guid}/process")]
    [ProducesResponseType(202)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> ProcessRevision(Guid revisionId, CancellationToken ct)
    {
        if(!Guid.TryParse(User.FindFirstValue("sub"),out var actor)) return Unauthorized();
        var result = await sender.Send(new Fire3D.Application.Ifc.Commands.ProcessRevision.ProcessRevisionCommand(actor, revisionId), ct);
        return result.IsSuccess ? Accepted($"/api/processing-jobs/{result.Value}", new { JobId = result.Value })
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }
}
