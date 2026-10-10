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
    /// <summary>Submits immutable scenario content and rubric for PlatformAdmin review. Technical readiness is a separate gate.</summary>
    [Authorize(Roles="OrganizationUser")]
    [HttpPost("/api/scenario-versions/{id:guid}/submit")]
    [ProducesResponseType<ContentReviewResponse>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    public Task<IActionResult> Submit(Guid id,[FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader]string? key,CancellationToken ct)=>Run("Submit",id,new{},key,ct);
    /// <summary>Approves only the exact submitted contentHash and rubricHash.</summary>
    [Authorize(Roles="PlatformAdmin")]
    [HttpPost("/api/admin/scenario-versions/{id:guid}/approve")]
    [ProducesResponseType<ContentReviewResponse>(200)]
    [ProducesResponseType<ProblemDetails>(409)]
    public Task<IActionResult> Approve(Guid id,ContentReviewDecisionRequest request,[FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader]string? key,CancellationToken ct)=>Decision("Approve",id,request,key,ct);
    /// <summary>Rejects submitted content with a reason. Further changes require a new version and submission.</summary>
    [Authorize(Roles="PlatformAdmin")]
    [HttpPost("/api/admin/scenario-versions/{id:guid}/reject")]
    [ProducesResponseType<ContentReviewResponse>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    public Task<IActionResult> Reject(Guid id,ContentReviewDecisionRequest request,[FromHeader(Name="Idempotency-Key"), Fire3D.API.OpenApi.RequiredRequestHeader]string? key,CancellationToken ct)=>Decision("Reject",id,request,key,ct);
    private Task<IActionResult> Decision(string action,Guid id,ContentReviewDecisionRequest input,string? key,CancellationToken ct)
    {
        var errors=new Dictionary<string,string[]>();
        bool Hash(string? value)=>value is{Length:64}&&value.All(char.IsAsciiHexDigit);
        if(!Hash(input.ContentHash))errors["contentHash"]=["Use the SHA-256 returned by submission."];
        if(!Hash(input.RubricHash))errors["rubricHash"]=["Use the SHA-256 returned by submission."];
        if(input.Reason?.Length>4000 || action=="Reject"&&string.IsNullOrWhiteSpace(input.Reason))errors["reason"]=["A rejection reason of 1-4000 characters is required."];
        if(errors.Count>0)return Task.FromResult<IActionResult>(Problem(statusCode:400,title:"Review validation failed.",extensions:new Dictionary<string,object?>{["code"]="VALIDATION_ERROR",["errors"]=errors}));
        return Run(action,id,input with{ContentHash=input.ContentHash.ToLowerInvariant(),RubricHash=input.RubricHash.ToLowerInvariant(),Reason=input.Reason?.Trim()},key,ct);
    }
    private async Task<IActionResult> Run(string action,Guid id,object input,string? key,CancellationToken ct)
    {
        var result=await store.ExecuteAsync(action,User.GetActorId(),User.GetSessionFamilyId(),id,null,input,key,ct);
        return result.IsSuccess?(action=="Submit"?StatusCode(201,result.Value):Ok(result.Value)):Problem(statusCode:result.Error!.Status,title:result.Error.Message,extensions:new Dictionary<string,object?>{["code"]=result.Error.Code,["errors"]=result.Error.Errors});
    }
}
