using Fire3D.Application.Editor;
using Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Fire3D.API.Controllers;

[ApiController]
public sealed class ScenarioRubricsController : ControllerBase
{
    /// <summary>
    /// Rubric contract published by the backend: supported schema versions (the canonical first version is the string "1"), default
    /// version, server-computed metrics, operators, criterion fields and limits. The same registry validates scenario drafts,
    /// Library rubric samples and AI drafts. No pass threshold, weight or criterion is suggested.
    /// </summary>
    /// <remarks>
    /// Metrics follow the runtime catalog (BE#183): with runtimeVersion, the core metrics plus the event metrics of that active runtime
    /// (the list a draft on that runtime is validated against); without it, the union over all active runtimes. 404 RUNTIME_NOT_FOUND
    /// when runtimeVersion is not an active runtime.
    /// </remarks>
    [Authorize(Roles="OrganizationUser,PlatformAdmin")]
    [HttpGet("api/scenario-rubrics/catalog")]
    [ProducesResponseType<RubricCatalog>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Catalog([FromQuery] string? runtimeVersion,[FromServices] IRuntimeCatalogReadStore runtimes,CancellationToken ct)
    {
        if (runtimeVersion is null) return Ok(RubricContract.CatalogFor(RuntimeCatalogContracts.Metrics(await runtimes.GetActiveCatalogAsync(ct))));
        var runtime=await runtimes.GetActiveRuntimeAsync(runtimeVersion,ct);
        return runtime is null
            ? Problem(statusCode:404,title:"Runtime version is not an active runtime.",extensions:new Dictionary<string,object?>{["code"]="RUNTIME_NOT_FOUND"})
            : Ok(RubricContract.CatalogFor(EditorContract.RuntimeMetrics(RuntimeCatalogContracts.For(runtime))));
    }
}
