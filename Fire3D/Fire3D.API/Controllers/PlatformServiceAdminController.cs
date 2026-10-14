using Fire3D.API.Authorization;
using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fire3D.API.Controllers;

/// <summary>
/// PlatformAdmin management of runtime compatibility versions and Building Trials. Runtime rows are immutable except isActive;
/// every change is audited. Deactivating a runtime blocks new publish, playtest and session starts that target it.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.PlatformAdministration)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType<ProblemDetails>(401)]
[ProducesResponseType<ProblemDetails>(403)]
public sealed class PlatformServiceAdminController(IPlatformServiceAdmin admin) : ControllerBase
{
    /// <summary>Every registered runtime version, newest first, active or not.</summary>
    [HttpGet("api/admin/runtime-catalog")]
    [ProducesResponseType<IReadOnlyList<RuntimeCatalogEntry>>(200)]
    public async Task<IActionResult> Runtimes(CancellationToken ct) => Reply(await admin.ListRuntimesAsync(User.GetActorId(), User.GetSessionFamilyId(), ct));

    /// <summary>Registers a runtime version (major.minor.patch) with its capabilities and capability contracts.</summary>
    /// <remarks>Contracts use the editor capability-contract schema and every contract key must be a listed capability; invalid input is
    /// 400 VALIDATION_ERROR with errors by JSONPath. A version can be registered once (409 RUNTIME_VERSION_EXISTS).</remarks>
    [HttpPost("api/admin/runtime-catalog")]
    [ProducesResponseType<RuntimeCatalogEntry>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> CreateRuntime(CreateRuntimeCatalogRequest request, CancellationToken ct)
    {
        var result = await admin.CreateRuntimeAsync(User.GetActorId(), User.GetSessionFamilyId(), request, ct);
        return result.IsSuccess ? Created("/api/admin/runtime-catalog", result.Value) : Problem(result.Error!);
    }

    /// <summary>Activates or deactivates a runtime version. Nothing else of a registered version can change.</summary>
    [HttpPatch("api/admin/runtime-catalog/{id:guid}")]
    [ProducesResponseType<RuntimeCatalogEntry>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> SetRuntimeActive(Guid id, SetRuntimeCatalogActiveRequest request, CancellationToken ct) =>
        Reply(await admin.SetRuntimeActiveAsync(User.GetActorId(), User.GetSessionFamilyId(), id, request.IsActive, ct));

    /// <summary>Trial and cancelled-Trial entitlements of one Building, newest first.</summary>
    [HttpGet("api/admin/buildings/{buildingId:guid}/trials")]
    [ProducesResponseType<IReadOnlyList<TrialEntitlementView>>(200)]
    [ProducesResponseType<ProblemDetails>(404)]
    public async Task<IActionResult> Trials(Guid buildingId, CancellationToken ct) =>
        Reply(await admin.ListTrialsAsync(User.GetActorId(), User.GetSessionFamilyId(), buildingId, ct));

    /// <summary>Grants a Trial starting now. Refused while a paid period (409 BUILDING_SERVICE_ACTIVE) or another Trial (409 TRIAL_ALREADY_ACTIVE) runs.</summary>
    [HttpPost("api/admin/buildings/{buildingId:guid}/trials")]
    [ProducesResponseType<TrialEntitlementView>(201)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> CreateTrial(Guid buildingId, CreateTrialRequest request, CancellationToken ct)
    {
        var result = await admin.CreateTrialAsync(User.GetActorId(), User.GetSessionFamilyId(), buildingId, request, ct);
        return result.IsSuccess ? Created($"/api/admin/buildings/{buildingId}/trials", result.Value) : Problem(result.Error!);
    }

    /// <summary>Adjusts the playtest units of a Trial, or cancels it.</summary>
    /// <remarks>Units never drop below the units already used (409 TRIAL_UNITS_BELOW_USED). Start and end dates are immutable;
    /// to extend, cancel the Trial and grant a new one.</remarks>
    [HttpPatch("api/admin/buildings/{buildingId:guid}/trials/{entitlementId:guid}")]
    [ProducesResponseType<TrialEntitlementView>(200)]
    [ProducesResponseType<ProblemDetails>(400)]
    [ProducesResponseType<ProblemDetails>(404)]
    [ProducesResponseType<ProblemDetails>(409)]
    public async Task<IActionResult> UpdateTrial(Guid buildingId, Guid entitlementId, UpdateTrialRequest request, CancellationToken ct) =>
        Reply(await admin.UpdateTrialAsync(User.GetActorId(), User.GetSessionFamilyId(), buildingId, entitlementId, request, ct));

    private IActionResult Reply<T>(AuthResult<T> result) => result.IsSuccess ? Ok(result.Value) : Problem(result.Error!);
    private ObjectResult Problem(AuthError error) => Problem(statusCode: error.Status, title: error.Message,
        extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["errors"] = error.Errors });
}
