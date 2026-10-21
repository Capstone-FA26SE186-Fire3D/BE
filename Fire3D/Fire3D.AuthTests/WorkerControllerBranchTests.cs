using System.Text.Json;
using Fire3D.API.Controllers;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

/// <summary>Machine-key worker controllers with a fake gate: validation branches, metadata contracts and what the worker sees.</summary>
public sealed class WorkerControllerBranchTests
{
    private const string Attempt = "11111111-1111-4111-8111-111111111111", Lease = "22222222-2222-4222-8222-222222222222";
    private static readonly string Hash = new('a', 64);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static T Controller<T>(T controller) where T : ControllerBase { controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }; return controller; }
    private static IProcessingWorkerGate Unreached() => ResetProxy.For<IProcessingWorkerGate>((method, _) => throw new Exception("Rejected input must not reach the gate: " + method));
    private static (int Status, string Code, IDictionary<string, string[]>? Errors) Problem(IActionResult action)
    {
        var result = Assert.IsType<ObjectResult>(action);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        problem.Extensions.TryGetValue("errors", out var errors);
        return (result.StatusCode!.Value, problem.Extensions["code"]!.ToString()!, errors as IDictionary<string, string[]>);
    }
    private static object Output(string type, object metadata, string schemaVersion = "1") => new
    {
        attemptId = Attempt, leaseToken = Lease,
        output = new { artifactType = type, objectKey = "worker-output/o/j/a/" + type, schemaVersion, validatorVersion = "fake-v1", sha256Hash = Hash, sizeBytes = 10, outcome = "Passed", metadata, issues = Array.Empty<object>() }
    };

    [Fact]
    public async Task Fail_requires_a_reason_and_then_forwards_the_attempt_to_the_gate()
    {
        var invalid = await Controller(new ProcessingWorkerController(Unreached(), Options.Create(new ProcessingWorkerOptions()))).Fail(Guid.NewGuid(), Json(new { attemptId = Attempt, leaseToken = Lease, reason = " " }), default);
        var problem = Problem(invalid);
        Assert.Equal((400, "VALIDATION_ERROR"), (problem.Status, problem.Code));
        Assert.Equal(["reason"], problem.Errors!.Keys);

        var job = Guid.NewGuid(); (string Action, Guid Job, JsonElement Input)? call = null;
        var gate = ResetProxy.For<IProcessingWorkerGate>((_, args) =>
        {
            call = ((string)args[0]!, (Guid)args[1]!, (JsonElement)args[2]!);
            return Task.FromResult(AuthResult<JsonElement>.Ok(Json(new { status = "Failed" })));
        });
        var ok = Assert.IsType<OkObjectResult>(await Controller(new ProcessingWorkerController(gate, Options.Create(new ProcessingWorkerOptions()))).Fail(job, Json(new { attemptId = Attempt, leaseToken = Lease, reason = "IFC parse error" }), default));
        Assert.Equal("Failed", ((JsonElement)ok.Value!).GetProperty("status").GetString());
        Assert.Equal(("Fail", job, "IFC parse error"), (call!.Value.Action, call.Value.Job, call.Value.Input.GetProperty("reason").GetString()));
        // Lease lengths are decided by the deployment on claim/renew only; a fail command is forwarded unchanged.
        Assert.False(call.Value.Input.TryGetProperty("leaseSeconds", out _));
    }

    [Fact]
    public async Task Unity_package_output_requires_runtime_metadata_before_the_gate()
    {
        var action = await Controller(new ProcessingWorkerController(Unreached(), Options.Create(new ProcessingWorkerOptions())))
            .Output(Guid.NewGuid(), Json(Output("unity_package", new { minRuntimeVersion = "1.0", requiredCapabilities = new object[] { "smoke", 3 } })), default);
        var problem = Problem(action);
        Assert.Equal((400, "VALIDATION_ERROR"), (problem.Status, problem.Code));
        Assert.Equal(["output.metadata.buildTarget", "output.metadata.manifestSchemaVersion", "output.metadata.minRuntimeVersion", "output.metadata.protocolVersion", "output.metadata.requiredCapabilities"],
            problem.Errors!.Keys.Order());
    }

    [Fact]
    public async Task Package_manifest_output_must_satisfy_fet3d_package_1()
    {
        var metadata = new
        {
            buildTarget = "Android", protocolVersion = "1", manifestSchemaVersion = "1", minRuntimeVersion = "2.0.0", runtimeVersion = "2.0.0",
            unityVersion = "6000.6.6f1", materialLibraryVersion = "1", requiredCapabilities = "door.exit",
            files = new[] { new { path = "floor_L1", sha256 = Hash, size = 0L } },
            floors = new[] { new { bundle = "floor_L1", lightingBundle = "floor_L1_lighting" } }
        };
        var action = await Controller(new ProcessingWorkerController(Unreached(), Options.Create(new ProcessingWorkerOptions())))
            .Output(Guid.NewGuid(), Json(Output("manifest", metadata, PackageManifest.SchemaVersion)), default);
        var result = Assert.IsType<ObjectResult>(action);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(422, result.StatusCode);
        Assert.Equal("PACKAGE_MANIFEST_INVALID", problem.Extensions["code"]);
        Assert.Equal(["metadata.files[0].size", "metadata.floors[0].lightingBundle", "metadata.requiredCapabilities"],
            ((Dictionary<string, string[]>)problem.Extensions["errors"]!).Keys.Order());
    }

    [Fact]
    public async Task Building_metadata_with_an_unknown_schema_version_is_refused()
    {
        var action = await Controller(new ProcessingWorkerController(Unreached(), Options.Create(new ProcessingWorkerOptions())))
            .Output(Guid.NewGuid(), Json(Output("building_metadata", new { schemaVersion = "fet3d.building/2" })), default);
        var problem = Problem(action);
        Assert.Equal((422, "BUILDING_METADATA_VERSION_UNSUPPORTED"), (problem.Status, problem.Code));
    }

    private static PackageWorkerController Packages(IProcessingWorkerGate gate, IPackageStorage? storage = null) =>
        Controller(new PackageWorkerController(gate, storage ?? new FakePackageStorage(), Options.Create(new ProcessingWorkerOptions { PackagePullEnabled = true, AllowedToolchains = ["fake-unity-v1"], PackageLeaseSeconds = 420 })));

    [Fact]
    public async Task Package_lease_rejects_a_non_text_build_target()
    {
        var problem = Problem(await Packages(Unreached()).Lease(Json(new { toolchainVersion = "fake-unity-v1", buildTarget = 7 }), default));
        Assert.Equal((400, "VALIDATION_ERROR"), (problem.Status, problem.Code));
        Assert.Equal(["buildTarget"], problem.Errors!.Keys);
    }

    [Fact]
    public async Task Package_lease_signs_geometry_inputs_and_hides_object_keys_and_internal_context()
    {
        var storage = new FakePackageStorage();
        storage.Objects["geometry/o/j/a/preview.glb"] = new byte[12];
        storage.Objects["geometry/o/j/a/building.json"] = new byte[5]; // size changed since acceptance: no URL
        var claim = Json(new
        {
            jobId = Guid.NewGuid(), attemptId = Attempt, leaseToken = Lease, outputPrefix = "worker-output/o/j/b/", pulled = true,
            packageManifest = new { objectKey = "secret/manifest.json" },
            geometryInputs = new object[]
            {
                new { artifactType = "preview_glb", artifactId = Guid.NewGuid(), objectKey = "geometry/o/j/a/preview.glb", sha256 = Hash, sizeBytes = 12L, schemaVersion = "fet3d.editor/1" },
                new { artifactType = "building_metadata", artifactId = Guid.NewGuid(), objectKey = "geometry/o/j/a/building.json", sha256 = Hash, sizeBytes = 9L, schemaVersion = "fet3d.building/1" }
            }
        });
        JsonElement? command = null;
        var gate = ResetProxy.For<IProcessingWorkerGate>((method, args) =>
        {
            Assert.Equal(nameof(IProcessingWorkerGate.LeasePackageAsync), method);
            command = (JsonElement)args[0]!;
            return Task.FromResult(AuthResult<JsonElement?>.Ok(claim));
        });
        var ok = Assert.IsType<OkObjectResult>(await Packages(gate, storage).Lease(Json(new { toolchainVersion = "fake-unity-v1" }), default));
        Assert.Equal(420, command!.Value.GetProperty("leaseSeconds").GetInt32());
        Assert.Equal(JsonValueKind.Null, command.Value.GetProperty("buildTarget").ValueKind);
        var body = (JsonElement)ok.Value!;
        var inputs = body.GetProperty("inputs").EnumerateArray().ToList();
        Assert.Equal("https://s3.test/geometry/o/j/a/preview.glb", inputs[0].GetProperty("url").GetString());
        Assert.Equal(12, inputs[0].GetProperty("sizeBytes").GetInt64());
        Assert.Equal("fet3d.editor/1", inputs[0].GetProperty("schemaVersion").GetString());
        Assert.Equal(JsonValueKind.Null, inputs[1].GetProperty("url").ValueKind);
        var text = body.GetRawText();
        foreach (var hidden in new[] { "objectKey", "geometryInputs", "packageManifest", "pulled", "secret/" }) Assert.DoesNotContain(hidden, text);
    }

    [Fact]
    public async Task Upload_urls_require_one_to_2001_files_with_positive_sizes()
    {
        var empty = Problem(await Packages(Unreached()).UploadUrls(Guid.NewGuid(), Json(new { attemptId = Attempt, leaseToken = Lease, files = Array.Empty<object>() }), default));
        Assert.Equal((400, "VALIDATION_ERROR"), (empty.Status, empty.Code));
        Assert.Equal(["files"], empty.Errors!.Keys);
        var zero = Problem(await Packages(Unreached()).UploadUrls(Guid.NewGuid(), Json(new { attemptId = Attempt, leaseToken = Lease, files = new[] { new { path = "floor_L1", sha256 = Hash, sizeBytes = 0 } } }), default));
        Assert.Equal(["files[0].sizeBytes"], zero.Errors!.Keys);
    }

    [Fact]
    public async Task Storage_checks_propagate_caller_cancellation_instead_of_reporting_unavailable()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var storage = ResetProxy.For<IPackageStorage>((_, args) => Task.FromCanceled<(long, string?)?>((CancellationToken)args[1]!));
        var package = Json(new { outputPrefix = "p/", packageManifest = new { schemaVersion = PackageManifest.SchemaVersion, objectKey = "p/manifest.json", sha256Hash = Hash, sizeBytes = 10, metadata = new { } } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PackageWorkerResponses.VerifyAsync(package, storage, cancelled.Token));
        var geometry = Json(new { kind = "Geometry", pulled = true, geometryOutputs = new[] { new { objectKey = "p/preview.glb", sizeBytes = 10, sha256Hash = Hash } } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PackageWorkerResponses.VerifyGeometryAsync(geometry, storage, cancelled.Token));
        // Without caller cancellation the same storage failure is a retryable 503.
        var down = ResetProxy.For<IPackageStorage>((_, _) => Task.FromException<(long, string?)?>(new TaskCanceledException("S3 timeout")));
        Assert.Equal("GEOMETRY_STORAGE_UNAVAILABLE", (await PackageWorkerResponses.VerifyGeometryAsync(geometry, down, default))?.Code);
    }
}
