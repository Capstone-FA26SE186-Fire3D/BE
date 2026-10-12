using Fire3D.Application.Editor;
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
    [Authorize(Roles="OrganizationUser,PlatformAdmin")]
    [HttpGet("api/scenario-rubrics/catalog")]
    [ProducesResponseType<RubricCatalog>(200)]
    [ProducesResponseType<ProblemDetails>(401)]
    [ProducesResponseType<ProblemDetails>(403)]
    public RubricCatalog Catalog()=>RubricContract.Catalog;
}
