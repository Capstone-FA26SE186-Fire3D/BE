using Fire3D.API.Authorization;
using Fire3D.Application.Authentication;
using Fire3D.Application.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Fire3D.API.Controllers;

[ApiController]
[Route("api/admin/audit-logs")]
[Authorize(Roles="PlatformAdmin")]
[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class AuditLogsController(IAuditQueries queries) : ControllerBase
{
    /// <summary>Reads immutable audit metadata. PlatformAdmin; default 30 days, maximum 90, UTC [from,to), stable createdAt/ID pagination.</summary>
    [HttpGet]
    [ProducesResponseType<AuditPage>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    public async Task<IActionResult> List([FromQuery]AuditFilters filters,CancellationToken ct)
    {
        var result=await queries.List(User.GetActorId(),User.GetSessionFamilyId(),filters,ct);
        return result.IsSuccess?Ok(result.Value):Failure(result.Error!);
    }
    /// <summary>Reads metadata and allowlisted field changes. Raw JSON, credentials, PII and support content are never returned.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<AuditDetail>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Get(Guid id,CancellationToken ct)
    {
        var result=await queries.Get(User.GetActorId(),User.GetSessionFamilyId(),id,ct);
        return result.IsSuccess?Ok(result.Value):Failure(result.Error!);
    }
    private ObjectResult Failure(AuthError error)=>Problem(statusCode:error.Status,title:error.Message,extensions:new Dictionary<string,object?>{["code"]=error.Code,["errors"]=error.Errors});
}