using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.API.Authorization;
using Fire3D.Application.Ifc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Fire3D.API.Controllers;

/// <summary>
/// BE#110: Unity package worker in pull mode (outbound-only VM). The worker leases a package job, downloads its inputs through
/// short signed URLs, uploads its output files through signed PUT URLs bound to each file's SHA-256, then registers outputs and
/// completes through the existing /internal/processing/jobs/{jobId} endpoints. Machine key only; user JWTs are not accepted.
/// </summary>
[ApiController, Authorize(AuthenticationSchemes = ProcessingWorkerAuthentication.SchemeName)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PackageWorkerController(IProcessingWorkerGate gate, IPackageStorage storage, IOptions<ProcessingWorkerOptions> options) : ControllerBase
{
    internal static readonly TimeSpan InputLifetime = TimeSpan.FromMinutes(15), UploadLifetime = TimeSpan.FromMinutes(30);

    /// <summary>Claims the oldest waiting PlaytestPackage/ReleasePackage job. 204 when nothing is waiting.</summary>
    [HttpPost("internal/processing/package-jobs/lease")]
    public async Task<IActionResult> Lease(JsonElement input, CancellationToken ct)
    {
        if (!options.Value.PackagePullEnabled) return Fail(409, "PACKAGE_PULL_DISABLED", "Package pull is not enabled on this deployment.");
        var errors = new Dictionary<string, string[]>();
        if (input.ValueKind != JsonValueKind.Object) return Fail(400, "VALIDATION_ERROR", "A lease request object is required.");
        if (Text(input, "toolchainVersion", 100) is not { } toolchain || !options.Value.AllowedToolchains.Contains(toolchain, StringComparer.Ordinal))
            errors["toolchainVersion"] = ["The toolchain must be explicitly allowed by this deployment."];
        string? target = null;
        if (input.TryGetProperty("buildTarget", out var raw) && raw.ValueKind != JsonValueKind.Null && (target = Text(input, "buildTarget", 100)) is null)
            errors["buildTarget"] = ["Use a build target such as Android, or omit it."];
        if (errors.Count > 0) return Fail(400, "VALIDATION_ERROR", "Lease request validation failed.", errors);
        var command = JsonSerializer.SerializeToElement(new { toolchainVersion = Text(input, "toolchainVersion", 100), buildTarget = target, leaseSeconds = options.Value.PackageLeaseSeconds });
        var result = await gate.LeasePackageAsync(command, ct);
        if (!result.IsSuccess) return Fail(result.Error!.Status, result.Error.Code, result.Error.Message);
        if (result.Value is not { } claim) return NoContent();
        return Ok(await PackageWorkerResponses.PresentAsync(claim, storage, ct));
    }

    /// <summary>Signs the input URLs again (they expire after 15 minutes) for the current attempt.</summary>
    [HttpPost("internal/processing/jobs/{jobId:guid}/inputs")]
    public async Task<IActionResult> Inputs(Guid jobId, JsonElement input, CancellationToken ct)
    {
        if (await LiveContextAsync(jobId, input, ct) is not { } context) return Stale();
        return Ok(await PackageWorkerResponses.PresentAsync(context, storage, ct, inputsOnly: true));
    }

    /// <summary>PUT URLs under the attempt's output prefix, one per file; each URL only accepts a body with the declared SHA-256.</summary>
    [HttpPost("internal/processing/jobs/{jobId:guid}/upload-urls")]
    public async Task<IActionResult> UploadUrls(Guid jobId, JsonElement input, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array
            || files.GetArrayLength() is 0 or > PackageManifest.MaxFiles + 1)
            return Fail(400, "VALIDATION_ERROR", "List 1-2001 files.", new() { ["files"] = [$"List 1-{PackageManifest.MaxFiles + 1} files."] });
        var requested = new List<(string Path, string Sha)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var file in files.EnumerateArray())
        {
            var at = $"files[{index++}]";
            var path = file.ValueKind == JsonValueKind.Object ? Text(file, "path", PackageManifest.MaxPathLength) : null;
            var sha = file.ValueKind == JsonValueKind.Object ? Text(file, "sha256", 64) : null;
            if (!PackageManifest.IsValidPath(path) || !seen.Add(path!)) errors[at + ".path"] = ["Use a unique relative path (letters, digits, '.', '_', '-', '/'; no '..')."];
            if (!PackageManifest.IsValidHash(sha)) errors[at + ".sha256"] = ["Use the lowercase hex SHA-256 of the file."];
            if (!file.TryGetProperty("sizeBytes", out var size) || !size.TryGetInt64(out var bytes) || bytes <= 0 || bytes > PackageManifest.MaxFileBytes)
                errors[at + ".sizeBytes"] = ["Use the file size in bytes (positive, single upload)."];
            if (path is not null && sha is not null) requested.Add((path, sha));
        }
        if (errors.Count > 0) return Fail(400, "VALIDATION_ERROR", "Upload request validation failed.", errors);
        if (await LiveContextAsync(jobId, input, ct) is not { } context) return Stale();
        var prefix = context.GetProperty("outputPrefix").GetString()!;
        var signed = new List<object>();
        foreach (var (path, sha) in requested)
        {
            var upload = await storage.SignPutAsync(prefix + path, sha, UploadLifetime, ct);
            signed.Add(new { path, url = upload.Url, headers = upload.Headers, expiresAt = upload.ExpiresAt });
        }
        return Ok(new { outputPrefix = prefix, files = signed });
    }

    private async Task<JsonElement?> LiveContextAsync(Guid jobId, JsonElement input, CancellationToken ct)
    {
        if (input.ValueKind != JsonValueKind.Object || !Id(input, "attemptId", out var attempt) || !Id(input, "leaseToken", out var token)) return null;
        var context = await gate.ContextAsync(attempt, token, ct);
        // Only a live package attempt of this job; a finished (Succeeded) attempt no longer uploads or downloads.
        return context is { } c && c.TryGetProperty("jobId", out var job) && job.GetString() == jobId.ToString() && c.TryGetProperty("outputPrefix", out _) ? c : null;
    }

    private ObjectResult Stale() => Fail(409, "ATTEMPT_STALE", "Use the current attempt and lease token of a running package job.");
    private ObjectResult Fail(int status, string code, string title, Dictionary<string, string[]>? errors = null) =>
        Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code, ["errors"] = errors });
    private static bool Id(JsonElement input, string name, out Guid id) { id = default; return input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out id) && id != Guid.Empty; }
    internal static string? Text(JsonElement input, string name, int max) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && text.Length <= max && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl) ? text : null;
}

/// <summary>Turns worker context (object keys) into what the worker sees (signed URLs only) and checks package storage before Complete.</summary>
public static class PackageWorkerResponses
{
    public static async Task<JsonElement> PresentAsync(JsonElement claim, IPackageStorage storage, CancellationToken ct, bool inputsOnly = false)
    {
        var node = JsonNode.Parse(claim.GetRawText())!.AsObject();
        if (node["geometryInputs"] is JsonArray geometry)
        {
            var inputs = new JsonArray();
            foreach (var item in geometry.OfType<JsonObject>())
            {
                var signed = await storage.SignVerifiedGetAsync(item["objectKey"]!.GetValue<string>(), item["sizeBytes"]!.GetValue<long>(), PackageWorkerController.InputLifetime, ct);
                inputs.Add(new JsonObject
                {
                    ["artifactType"] = item["artifactType"]?.DeepClone(), ["artifactId"] = item["artifactId"]?.DeepClone(), ["sha256"] = item["sha256"]?.DeepClone(),
                    ["sizeBytes"] = item["sizeBytes"]?.DeepClone(), ["schemaVersion"] = item["schemaVersion"]?.DeepClone(),
                    // null: the object is missing or its size changed; the worker fails the job instead of building from other bytes.
                    ["url"] = signed?.Url, ["expiresAt"] = signed?.ExpiresAt
                });
            }
            node["inputs"] = inputs;
        }
        node.Remove("geometryInputs");
        node.Remove("packageManifest");
        if (inputsOnly) return JsonSerializer.SerializeToElement(new JsonObject { ["inputs"] = node["inputs"]?.DeepClone() ?? new JsonArray(), ["outputPrefix"] = node["outputPrefix"]?.DeepClone() });
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>
    /// Before a fet3d.package/1 package completes: manifest.json and every listed file exist under the attempt prefix with the declared
    /// size and S3-verified SHA-256, and manifest.json lists the same files and floors as the registered metadata. Null when fine.
    /// </summary>
    public static async Task<(int Status, string Code, string Message)?> VerifyAsync(JsonElement context, IPackageStorage storage, CancellationToken ct)
    {
        if (!context.TryGetProperty("packageManifest", out var manifest) || manifest.ValueKind != JsonValueKind.Object
            || manifest.GetProperty("schemaVersion").GetString() != PackageManifest.SchemaVersion) return null;
        var prefix = context.GetProperty("outputPrefix").GetString()!;
        var mismatch = (409, "PACKAGE_STORAGE_MISMATCH", "Uploaded package files do not match the registered manifest.");
        try
        {
            var key = manifest.GetProperty("objectKey").GetString()!;
            var sha = manifest.GetProperty("sha256Hash").GetString()!;
            var head = await storage.HeadAsync(key, ct);
            if (head is not { } h || h.Size != manifest.GetProperty("sizeBytes").GetInt64() || h.Sha256Hex != sha) return mismatch;
            if (await storage.ReadAsync(key, PackageManifest.MaxManifestBytes, ct) is not { } bytes || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != sha) return mismatch;
            var registered = JsonNode.Parse(manifest.GetProperty("metadata").GetRawText());
            JsonNode? uploaded;
            try { uploaded = JsonNode.Parse(bytes); } catch (JsonException) { return mismatch; }
            if (!JsonNode.DeepEquals(uploaded?["files"], registered?["files"]) || !JsonNode.DeepEquals(uploaded?["floors"], registered?["floors"])) return mismatch;
            using var gateLimit = new SemaphoreSlim(16);
            var checks = registered!["files"]!.AsArray().OfType<JsonObject>().Select(async file =>
            {
                await gateLimit.WaitAsync(ct);
                try
                {
                    var stored = await storage.HeadAsync(prefix + file["path"]!.GetValue<string>(), ct);
                    return stored is { } s && s.Size == file["size"]!.GetValue<long>() && s.Sha256Hex == file["sha256"]!.GetValue<string>();
                }
                finally { gateLimit.Release(); }
            }).ToList();
            return (await Task.WhenAll(checks)).All(x => x) ? null : mismatch;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return (503, "PACKAGE_STORAGE_UNAVAILABLE", "Package storage could not be checked; retry Complete."); }
    }

    /// <summary>Signed GET URLs (10 minutes) for the pinned manifest and each file; the SHA-256 values come from the accepted manifest.</summary>
    public static async Task<RuntimePackageDownload> DownloadAsync(JsonElement pinned, IPackageStorage storage, CancellationToken ct)
    {
        var lifetime = TimeSpan.FromMinutes(10);
        var prefix = pinned.GetProperty("prefix").GetString()!;
        var manifest = await storage.SignGetAsync(pinned.GetProperty("manifestKey").GetString()!, lifetime, ct);
        var files = new List<RuntimePackageFile>();
        foreach (var file in pinned.GetProperty("files").EnumerateArray())
        {
            var path = file.GetProperty("path").GetString()!;
            files.Add(new(path, (await storage.SignGetAsync(prefix + path, lifetime, ct)).Url, file.GetProperty("sha256").GetString()!, file.GetProperty("size").GetInt64()));
        }
        return new(PackageManifest.SchemaVersion, new(manifest.Url, pinned.GetProperty("manifestSha256").GetString()!, pinned.GetProperty("manifestSizeBytes").GetInt64()),
            files, pinned.GetProperty("floors").Clone(), pinned.GetProperty("runtimeVersion").GetString(), pinned.GetProperty("buildTarget").GetString(), manifest.ExpiresAt);
    }
}

/// <summary>Download a fet3d.package/1 package: verify manifest.json against manifest.sha256, then every file against its sha256, before launching Unity.</summary>
public sealed record RuntimePackageDownload(string SchemaVersion, RuntimePackageManifestFile Manifest, IReadOnlyList<RuntimePackageFile> Files, JsonElement Floors,
    string? RuntimeVersion, string? BuildTarget, DateTimeOffset ExpiresAt);
public sealed record RuntimePackageManifestFile(string Url, string Sha256, long SizeBytes);
/// <summary>path is relative to the package folder (the folder of manifest.json).</summary>
public sealed record RuntimePackageFile(string Path, string Url, string Sha256, long SizeBytes);
