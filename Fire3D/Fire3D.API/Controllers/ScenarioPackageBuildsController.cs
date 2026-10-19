using Fire3D.API.Authorization;
using Fire3D.Application.Ifc;
using Fire3D.Application.Scenarios;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
namespace Fire3D.API.Controllers;
[ApiController]
[Authorize(Roles="OrganizationUser,PlatformAdmin")]
public sealed class ScenarioPackageBuildsController(IScenarioPackageBuildStore store,IOptions<ProcessingWorkerOptions> options):ControllerBase
{
    /// <summary>Queues a package build from a verified source and immutable v7 version. A queued job is not an accepted Unity package.</summary>
    /// <remarks>202 confirms the PostgreSQL job/outbox. Http is the default transport; RedisStreams is optional. Publication and durable handoff are separate from accepted package output.
    /// buildTarget must be one the package worker serves (ProcessingWorker:PackageBuildTargets, default Android), otherwise 400 BUILD_TARGET_UNSUPPORTED.
    /// A revision runs one job at a time: while another job of the revision is Queued/Running the answer is 409 PROCESSING_JOB_ACTIVE with errors.jobId.</remarks>
    [HttpPost("/api/scenario-versions/{id:guid}/package-builds")]
    [ProducesResponseType(202)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Build(Guid id,[FromBody]PackageBuildRequest request,[FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader]string? key,CancellationToken ct)
    {
        if(request.Kind is not ("PlaytestPackage" or "ReleasePackage") || string.IsNullOrWhiteSpace(request.BuildTarget) || request.BuildTarget.Length>100)
            return Problem(statusCode:400,title:"Use PlaytestPackage or ReleasePackage and a build target of 1-100 characters.",extensions:new Dictionary<string,object?>{["code"]="VALIDATION_ERROR",["errors"]=new{kind=new[]{"Check package kind and buildTarget."}}});
        // BE#131: a job for a target no worker serves stays Queued and blocks the revision's later builds.
        if(!options.Value.PackageBuildTargets.Contains(request.BuildTarget,StringComparer.Ordinal))
            return Problem(statusCode:400,title:"No package worker builds this target.",extensions:new Dictionary<string,object?>{["code"]="BUILD_TARGET_UNSUPPORTED",["errors"]=new{buildTarget=new[]{"Use one of: "+string.Join(", ",options.Value.PackageBuildTargets)+"."}}});
        var result=await store.BuildAsync(User.GetActorId(),id,request,key,ct);
        return result.IsSuccess?Accepted($"/api/processing-jobs/{result.Value}",new{jobId=result.Value}):Problem(statusCode:result.Error!.Status,title:result.Error.Message,extensions:new Dictionary<string,object?>{["code"]=result.Error.Code,["errors"]=result.Error.Errors});
    }
}
