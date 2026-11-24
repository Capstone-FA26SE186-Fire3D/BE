using Fire3D.API.Authorization;
using Fire3D.Application.FireSafety;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

/// <summary>"Hồ sơ phương án trực quan" of a Building (BE#215, FR-PLAN-12). OrganizationUser of the Building or PlatformAdmin.</summary>
[ApiController]
[Authorize(Roles = "OrganizationUser,PlatformAdmin")]
[ProducesResponseType<ProblemDetails>(401)]
[ProducesResponseType<ProblemDetails>(403)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PlanDossierController(IPlanDossierService dossiers) : ControllerBase
{
    /// <summary>The Building's plan in the order of Mẫu PC06: header, I site layout, II facility, III hazard, IV.1 situations, V history.</summary>
    /// <remarks>Built on the newest Approved fire infrastructure of the Building, or on infrastructureId (422 FIRE_INFRASTRUCTURE_NOT_APPROVED
    /// when that version is not an Approved one of this Building). Without any Approved infrastructure siteLayout is null and scenarios is
    /// empty. coverage tells whether Approved situations cover InHours and OffHours, which publishing requires. Another organization gets 404.</remarks>
    [HttpGet("/api/buildings/{id:guid}/plan-dossier")]
    [ProducesResponseType<PlanDossierResponse>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(422)]
    public async Task<IActionResult> Get(Guid id, [FromQuery] Guid? infrastructureId, CancellationToken ct)
    {
        var result = await dossiers.GetAsync(User.GetActorId(), User.GetSessionFamilyId(), id, infrastructureId, ct);
        return result.IsSuccess ? Ok(result.Value)
            : Problem(statusCode: result.Error!.Status, title: result.Error.Message,
                extensions: new Dictionary<string, object?> { ["code"] = result.Error.Code, ["errors"] = result.Error.Errors });
    }
}
