using Fire3D.API.Authorization;
using Fire3D.Application.Scenarios;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Fire3D.API.Controllers;
[ApiController]
[Authorize(Roles="OrganizationUser,PlatformAdmin")]
public sealed class ScenarioPackageBuildsController(IScenarioPackageBuildStore store):ControllerBase
{
    /// <summary>Queues a package build from a verified source and immutable v7 version. A queued job is not an accepted Unity package.</summary>
    /// <remarks>202 confirms the PostgreSQL job/outbox. Http is the default transport; RedisStreams is optional. Publication and durable handoff are separate from accepted package output.</remarks>
    [HttpPost("/api/scenario-versions/{id:guid}/package-builds")]
    [ProducesResponseType(202)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> Build(Guid id,[FromBody]PackageBuildRequest request,[FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader]string? key,CancellationToken ct)
    {
        if(request.Kind is not ("PlaytestPackage" or "ReleasePackage") || string.IsNullOrWhiteSpace(request.BuildTarget) || request.BuildTarget.Length>100)
            return Problem(statusCode:400,title:"Use PlaytestPackage or ReleasePackage and a build target of 1-100 characters.",extensions:new Dictionary<string,object?>{["code"]="VALIDATION_ERROR",["errors"]=new{kind=new[]{"Check package kind and buildTarget."}}});
        var result=await store.BuildAsync(User.GetActorId(),id,request,key,ct);
        return result.IsSuccess?Accepted($"/api/processing-jobs/{result.Value}",new{jobId=result.Value}):Problem(statusCode:result.Error!.Status,title:result.Error.Message,extensions:new Dictionary<string,object?>{["code"]=result.Error.Code,["errors"]=result.Error.Errors});
    }
}
