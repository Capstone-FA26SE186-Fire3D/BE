using Fire3D.API.Authorization;
using Fire3D.Application.Scenarios;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Fire3D.API.Controllers;
[ApiController]
[ProducesResponseType<ProblemDetails>(401)]
[ProducesResponseType<ProblemDetails>(403)]
[ProducesResponseType<ProblemDetails>(404)]
[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class ScenarioContentReviewsController(IScenarioReadinessStore store):ControllerBase
{
    /// <summary>Submits immutable scenario content and rubric for thẩm định by the assigned FireSafetyExpert (BE#242). Technical readiness is a separate gate.</summary>
    [Authorize(Roles="OrganizationUser")]
    [HttpPost("/api/scenario-versions/{id:guid}/submit")]
    [ProducesResponseType<ContentReviewResponse>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    public Task<IActionResult> Submit(Guid id,[FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader]string? key,CancellationToken ct)=>Run("Submit",id,new{},key,ct);
    /// <summary>Removed (BE#245): PlatformAdmin no longer approves scenario content. Thẩm định is done by the assigned FireSafetyExpert
    /// through POST /api/expert/reviews/{id}/approve (BE#211, BE#242). Always 410 ADMIN_REVIEW_REMOVED.</summary>
    [Obsolete("Removed (BE#245): use /api/expert/reviews/{id}/approve as the assigned FireSafetyExpert.")]
    [Authorize(Roles="PlatformAdmin")]
    [HttpPost("/api/admin/scenario-versions/{id:guid}/approve")]
    [ProducesResponseType<ProblemDetails>(410)]
    public IActionResult Approve(Guid id)=>Removed();
    /// <summary>Removed (BE#245): PlatformAdmin no longer rejects scenario content. Use POST /api/expert/reviews/{id}/reject as the
    /// assigned FireSafetyExpert. Always 410 ADMIN_REVIEW_REMOVED.</summary>
    [Obsolete("Removed (BE#245): use /api/expert/reviews/{id}/reject as the assigned FireSafetyExpert.")]
    [Authorize(Roles="PlatformAdmin")]
    [HttpPost("/api/admin/scenario-versions/{id:guid}/reject")]
    [ProducesResponseType<ProblemDetails>(410)]
    public IActionResult Reject(Guid id)=>Removed();
    private ObjectResult Removed()=>Problem(statusCode:410,title:"PlatformAdmin no longer decides thẩm định; the assigned FireSafetyExpert decides through /api/expert/reviews/{id}.",
        extensions:new Dictionary<string,object?>{["code"]="ADMIN_REVIEW_REMOVED"});
    private async Task<IActionResult> Run(string action,Guid id,object input,string? key,CancellationToken ct)
    {
        var result=await store.ExecuteAsync(action,User.GetActorId(),User.GetSessionFamilyId(),id,null,input,key,ct);
        return result.IsSuccess?(action=="Submit"?StatusCode(201,result.Value):Ok(result.Value)):Problem(statusCode:result.Error!.Status,title:result.Error.Message,extensions:new Dictionary<string,object?>{["code"]=result.Error.Code,["errors"]=result.Error.Errors});
    }
}
