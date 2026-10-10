using Fire3D.API.Authorization;
using Fire3D.Application.Ifc;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
namespace Fire3D.API.Controllers;
[ApiController]
[Authorize(Roles="OrganizationUser,PlatformAdmin")]
[EnableRateLimiting("administration")]
[Route("api")]
[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class IfcCommandsController(ISender sender) : ControllerBase
{
    /// <summary>Records a rejection for one revisionâ€“scenario version pair without changing the shared revision status.</summary>
    [HttpPost("revisions/{revisionId:guid}/reviews")]
    [ProducesResponseType(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> RejectScenarioVersion(Guid revisionId,
        Fire3D.Application.Scenarios.Commands.RejectScenarioVersion.RejectScenarioVersionRequest request, CancellationToken ct)
    {
        var actor = User.GetActorId();
        var result = await sender.Send(
            new Fire3D.Application.Scenarios.Commands.RejectScenarioVersion.RejectScenarioVersionCommand(actor, User.GetSessionFamilyId(), revisionId, request), ct);
        return result.IsSuccess ? Created($"/api/revisions/{revisionId}/reviews/{result.Value}", new { Id = result.Value })
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    /// <summary>Requeue má»™t processing job Failed qua gate database vÃ  transactional outbox.</summary>
    /// <remarks>
    /// OrganizationUser Ä‘Ãºng tenant hoáº·c PlatformAdmin. Body: requestId (UUID má»›i cho má»™t Ã½ Ä‘á»‹nh retry),
    /// reason (1-1000 kÃ½ tá»±). Gá»­i láº¡i Ä‘Ãºng requestId/reason tráº£ AlreadyRequeued, ká»ƒ cáº£ job Ä‘Ã£ cháº¡y xong sau retry.
    /// CÃ¹ng key khÃ¡c job/reason tráº£ 409; key má»›i chá»‰ dÃ¹ng vá»›i Failed. Cancelled/Succeeded khÃ´ng tá»± cháº¡y láº¡i.
    /// Response 202 cÃ³ jobId/outcome vÃ  Location theo dÃµi job. 202 xÃ¡c nháº­n giao viá»‡c bá»n vá»¯ng, chÆ°a xÃ¡c nháº­n worker hoÃ n táº¥t.
    /// Cáº§n gate requeue_processing_job, outbox vÃ  quyá»n DB theo v6.7; khÃ´ng ghi trá»±c tiáº¿p status Ä‘á»ƒ bá» qua gate.
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
        var actor = User.GetActorId();
        var result=await sender.Send(new RetryProcessingJobCommand(actor,jobId,request),ct);
        return result.IsSuccess ? Accepted($"/api/processing-jobs/{jobId}",result.Value)
            : Problem(statusCode:result.Error!.Status,title:result.Error.Message,
                extensions:new Dictionary<string,object?> {["code"]=result.Error.Code});
    }

    [HttpPost("buildings/{buildingId:guid}/ifc")]
    [ProducesResponseType<Fire3D.Application.Ifc.Commands.InitiateUpload.InitiateIfcUploadResponse>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<ActionResult<Fire3D.Application.Ifc.Commands.InitiateUpload.InitiateIfcUploadResponse>> InitiateUpload(Guid buildingId,
        Fire3D.Application.Ifc.Commands.InitiateUpload.InitiateIfcUploadRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct)
    {
        var actor = User.GetActorId();
        var result = await sender.Send(new Fire3D.Application.Ifc.Commands.InitiateUpload.InitiateIfcUploadCommand(actor, buildingId, request, key), ct);
        return result.IsSuccess ? Created($"/api/revisions/{result.Value!.RevisionId}", result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    [HttpPost("revisions/{revisionId:guid}/upload-complete")]
    [ProducesResponseType(204)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    [ProducesResponseType<ProblemDetails>(422)]
    [ProducesResponseType<ProblemDetails>(410)]
    [ProducesResponseType<ProblemDetails>(503)]
    public async Task<IActionResult> FinalizeUpload(Guid revisionId,
        Fire3D.Application.Ifc.Commands.FinalizeUpload.FinalizeIfcUploadRequest request, CancellationToken ct)
    {
        var actor = User.GetActorId();
        var result = await sender.Send(new Fire3D.Application.Ifc.Commands.FinalizeUpload.FinalizeIfcUploadCommand(actor, revisionId, request), ct);
        if (result.Error?.RetryAfterSeconds is int retry) Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return result.IsSuccess ? NoContent()
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    /// <summary>Queues verified IFC processing durably in PostgreSQL.</summary>
    /// <remarks>202 does not mean processing succeeded. Deployment selects Http or RedisStreams transport. Redis Published means publication; ACK means durable worker handoff. Neither means accepted output.</remarks>
    [HttpPost("revisions/{revisionId:guid}/process")]
    [ProducesResponseType(202)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> ProcessRevision(Guid revisionId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct)
    {
        var actor = User.GetActorId();
        var result = await sender.Send(new Fire3D.Application.Ifc.Commands.ProcessRevision.ProcessRevisionCommand(actor, revisionId, key), ct);
        return result.IsSuccess ? Accepted($"/api/processing-jobs/{result.Value}", new { JobId = result.Value })
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }

    /// <summary>
    /// Records technical readiness for the exact revision/version/validation/artifact pair; it does not approve content.
    /// </summary>
    [HttpPost("revisions/{revisionId:guid}/confirm-for-training")]
    [ProducesResponseType(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> ConfirmForTraining(Guid revisionId, [FromBody] Fire3D.Application.Scenarios.ConfirmTrainingRequest request, CancellationToken ct)
    {
        var actor = User.GetActorId();

        var result = await sender.Send(new Fire3D.Application.Ifc.Commands.ConfirmForTraining.ConfirmForTrainingCommand(actor, User.GetSessionFamilyId(), revisionId, request), ct);

        return result.IsSuccess 
            ? Ok(new { reviewId = result.Value })
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,extensions:new Dictionary<string,object?>{["code"]=result.Error.Code,["errors"]=result.Error.Errors});
    }
}
