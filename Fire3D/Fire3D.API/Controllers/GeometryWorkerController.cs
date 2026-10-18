using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.API.Authorization;
using Fire3D.Application.Ifc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Fire3D.API.Controllers;

/// <summary>
/// Geometry pull mode: the IFC/Geometry worker runs beside the Unity package worker on the outbound-only VM and leases Geometry jobs
/// itself, like package jobs. It downloads the verified IFC source through a short signed URL, uploads its outputs through
/// /internal/processing/jobs/{jobId}/upload-urls, then registers outputs and completes through /internal/processing/jobs/{jobId}.
/// Machine key only; user JWTs are not accepted. The worker never receives storage or database credentials.
/// </summary>
[ApiController, Authorize(AuthenticationSchemes = ProcessingWorkerAuthentication.SchemeName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class GeometryWorkerController(IProcessingWorkerGate gate, IPackageStorage storage, IOptions<ProcessingWorkerOptions> options) : ControllerBase
{
    private static readonly string[] Fields = ["jobId", "kind", "attemptId", "leaseToken", "leaseUntil", "receiptId", "revisionId", "outputPrefix", "inputHash", "replayed", "inputs"];

    /// <summary>Claims the oldest waiting Geometry job with a GeometryLeaseSeconds lease. 204 when nothing is waiting.</summary>
    [HttpPost("internal/processing/geometry-jobs/lease")]
    public async Task<IActionResult> Lease(JsonElement input, CancellationToken ct)
    {
        if (!options.Value.GeometryPullEnabled) return Fail(409, "GEOMETRY_PULL_DISABLED", "Geometry pull is not enabled on this deployment.");
        if (input.ValueKind != JsonValueKind.Object) return Fail(400, "VALIDATION_ERROR", "A lease request object is required.");
        if (PackageWorkerController.Text(input, "toolchainVersion", 100) is not { } toolchain || !options.Value.AllowedToolchains.Contains(toolchain, StringComparer.Ordinal))
            return Fail(400, "VALIDATION_ERROR", "Lease request validation failed.",
                new() { ["toolchainVersion"] = ["The toolchain must be explicitly allowed by this deployment."] });
        var command = JsonSerializer.SerializeToElement(new { toolchainVersion = toolchain, leaseSeconds = options.Value.GeometryLeaseSeconds });
        var result = await gate.LeaseGeometryAsync(command, ct);
        if (!result.IsSuccess) return Fail(result.Error!.Status, result.Error.Code, result.Error.Message);
        if (result.Value is not { } claim) return NoContent();
        var presented = JsonNode.Parse((await PackageWorkerResponses.PresentAsync(claim, storage, ct, signSource: true)).GetRawText())!.AsObject();
        var body = new JsonObject();
        foreach (var field in Fields) body[field] = presented[field]?.DeepClone();
        body["inputs"] ??= new JsonArray();
        return Ok(body);
    }

    private ObjectResult Fail(int status, string code, string title, Dictionary<string, string[]>? errors = null) =>
        Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code, ["errors"] = errors });
}
