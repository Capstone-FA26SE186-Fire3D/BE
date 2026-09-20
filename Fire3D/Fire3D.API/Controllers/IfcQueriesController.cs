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
    /// <summary>Đọc logical job và attempt hiện hành của pipeline IFC.</summary>
    /// <remarks>
    /// Cần OrganizationUser đúng tenant hoặc PlatformAdmin; Trainee không có quyền.
    /// Response gồm job, inputHash, currentAttemptId và currentAttempt (null khi chưa claim).
    /// Attempt gồm số lần chạy, trạng thái, toolchain, startedAt/finishedAt và outputHash.
    /// Không trả lease token, worker credential hoặc raw error log. Job sai tenant/không tồn tại trả 404.
    /// Yêu cầu schema processing_job_attempts/current_attempt_id theo thiết kế v6.7.
    /// </remarks>
    [HttpGet("processing-jobs/{jobId:guid}")]
    [ProducesResponseType<ProcessingJobDetailResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<ActionResult<ProcessingJobDetailResponse>> GetJob(Guid jobId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();
        var result = await sender.Send(new GetProcessingJobQuery(actor, jobId), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status,
            title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }
    /// <summary>Đọc một validation run cùng provenance job/attempt.</summary>
    /// <remarks>
    /// OrganizationUser đúng tenant hoặc PlatformAdmin; Trainee bị từ chối.
    /// Trả scope,validatorVersion,status,summary,artifactId,scenarioVersionId,processingJobId,
    /// processingAttemptId và timestamps. Có thể đọc run lịch sử; không xem run lịch sử là QA hiện hành.
    /// Run không tồn tại/khác tenant trả 404. Summary giữ kết quả worker, không tự suy ra Passed khi chưa hoàn tất.
    /// </remarks>
    [HttpGet("validation-runs/{validationRunId:guid}")]
    [ProducesResponseType<ValidationRunResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<ActionResult<ValidationRunResponse>> GetValidation(Guid validationRunId, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();
        var result = await sender.Send(new GetValidationRunQuery(actor, validationRunId), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status,
            title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }
    /// <summary>Đọc QA của attempt hiện hành, không trộn kết quả lịch sử.</summary>
    /// <remarks>
    /// Cần OrganizationUser đúng tenant hoặc PlatformAdmin. page mặc định 1; pageSize 20, tối đa 100.
    /// Trả jobId,currentAttemptId,validationRuns {items,totalCount,page,pageSize}.
    /// Chưa có attempt/run trả danh sách rỗng, không tự đánh dấu Passed. Đọc run cũ qua GET validation-runs/{id}.
    /// Job không tồn tại/ngoài scope trả 404; Trainee trả 403.
    /// </remarks>
    [HttpGet("processing-jobs/{jobId:guid}/qa")]
    [ProducesResponseType<JobQaResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<ActionResult<JobQaResponse>> GetJobQa(Guid jobId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var actor)) return Unauthorized();
        var result = await sender.Send(new GetJobQaQuery(actor, jobId, page, pageSize), ct);
        return result.IsSuccess ? Ok(result.Value) : Problem(statusCode: result.Error!.Status,
            title: result.Error.Message, extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code });
    }
}
