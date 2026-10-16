using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Ifc;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // IFC-03: only IFC is accepted, by name at initiation and by the STEP header at completion.
    [PostgresFact]
    public async Task Ifc_upload_refuses_non_ifc_names_and_content()
    {
        var building = await SeedIfcBuilding(); var fake = new IfcMemoryStorage(); await using var context = BuildingContext(testConnection);
        var flow = new IfcUploadService(new IfcUploadStore(context), fake.Storage, fake.Inspector, IfcOptions, TimeProvider.System);
        var rvt = await flow.InitiateAsync(adminId, building, new(IfcBytes.Length, "model.rvt", "v1", IfcHash), "rvt-key", default);
        Assert.Equal(("IFC_UNSUPPORTED_FORMAT", 400), (rvt.Error?.Code, rvt.Error?.Status));
        Assert.Contains("originalFilename", rvt.Error!.Errors!.Keys);
        Assert.Equal("VALIDATION_ERROR", (await flow.InitiateAsync(adminId, building, new(IfcBytes.Length, "", "v1", IfcHash), "blank-key", default)).Error?.Code);

        var zip = Encoding.UTF8.GetBytes("PK\u0003\u0004 a renamed archive, not IFC");
        var zipHash = Convert.ToHexStringLower(SHA256.HashData(zip));
        var upload = (await flow.InitiateAsync(adminId, building, new(zip.Length, "renamed.ifc", "v2", zipHash), "zip-key", default)).Value!;
        fake.Put(upload.ObjectKey, zip);
        var completed = await flow.CompleteAsync(adminId, upload.RevisionId, new(upload.ObjectKey, zip.Length, "application/octet-stream", zipHash, "renamed.ifc"), default);
        Assert.Equal(("IFC_UNSUPPORTED_FORMAT", 422), (completed.Error?.Code, completed.Error?.Status));
        Assert.Equal(0, fake.Copies);
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM source_documents WHERE revision_id='{upload.RevisionId}' AND upload_verified_at IS NOT NULL"));
    }

    // IFC-03: a failed Geometry QA leaves the revision NeedsFix and its issues name the IFC elements.
    [PostgresFact]
    public async Task Failed_geometry_qa_marks_needs_fix_and_issues_carry_ifc_elements()
    {
        var f = await FinalReviewGeometry("Failed", [new { severity = "Error", code = "IFC_NO_BODY_GEOMETRY", message = "Walls have no Body representation.",
            ifcGlobalIds = new[] { "1hOSvn6df7F8_7GcBWlRGQ", "1hOSvn6df7F8_7GcBWlRGR" } }]);
        Assert.Equal("NeedsFix", (string?)await ScalarAsync($"SELECT status::text FROM revisions WHERE id='{f.Revision}'"));
        Assert.Equal("1hOSvn6df7F8_7GcBWlRGQ", (string?)await ScalarAsync($"SELECT ifc_guid FROM revision_issues WHERE validation_run_id='{f.Run}'"));
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var issues = (await client.GetFromJsonAsync<JsonObject>($"/api/revisions/{f.Revision}/issues"))!["items"]!.AsArray();
        var issue = Assert.Single(issues)!;
        Assert.Equal("IFC_NO_BODY_GEOMETRY", issue["issueCode"]!.GetValue<string>());
        Assert.Equal(2, issue["ifcGlobalIds"]!.AsArray().Count);

        // A passed run still makes the revision ReadyForScenario.
        var passed = await FinalReviewGeometry("Passed", []);
        Assert.Equal("ReadyForScenario", (string?)await ScalarAsync($"SELECT status::text FROM revisions WHERE id='{passed.Revision}'"));
    }

    // IFC-03: bim_facts exists in the migrations and holds IfcSpace Name/LongName/Category from building_metadata.spaces.
    [PostgresFact]
    public async Task Bim_facts_hold_space_names_from_building_metadata()
    {
        var revision = await SeedVerifiedIfcRevision();
        var metadata = BuildingMetadataValidatorTests.Fixture();
        metadata["spaces"]!.AsArray().Add(new JsonObject { ["ifcGlobalId"] = "2O2Fr$t4X7Zf8NOew3FLOH", ["category"] = "stair" });
        metadata["spaces"]!.AsArray().Add(new JsonObject { ["ifcGlobalId"] = "not-a-global-id", ["name"] = "skipped" });
        await SeedEquipmentGeometry(revision, metadata);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var facts = (await client.GetFromJsonAsync<JsonObject>($"/api/revisions/{revision}/bim-facts?pageSize=100"))!["items"]!.AsArray();
        Assert.Equal(6, facts.Count);
        var longName = facts.Single(x => x!["ifcGlobalId"]!.GetValue<string>() == "3cUkl32yn9qRSPvBJVyWYp" && x["propertyPath"]!.GetValue<string>() == "LongName")!;
        Assert.Equal("Corridor", longName["value"]!.GetValue<string>());
        Assert.Equal("IfcSpace", longName["entityType"]!.GetValue<string>());
        Assert.Equal(IfcHash, longName["sourceHash"]!.GetValue<string>());
        var unnamed = facts.Single(x => x!["ifcGlobalId"]!.GetValue<string>() == "2O2Fr$t4X7Zf8NOew3FLOH" && x["propertyPath"]!.GetValue<string>() == "Name")!;
        Assert.Equal("SPACE_UNNAMED", unnamed["qualityFlags"]![0]!.GetValue<string>());
    }

    [PostgresFact]
    public async Task Ifc_qa_codes_and_issue_elements_are_published_in_openapi()
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var issue = doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("RevisionIssueResponse").GetProperty("properties");
        Assert.Contains("IFC_NO_SPACES", issue.GetProperty("issueCode").GetProperty("description").GetString());
        Assert.True(issue.TryGetProperty("ifcGlobalIds", out _));
    }
}

public sealed class IfcQaContractUnitTests
{
    [Theory]
    [InlineData("ISO-10303-21;HEADER;", true)]
    [InlineData("﻿\r\n  ISO-10303-21;", true)]
    [InlineData("PK\u0003\u0004", false)]
    [InlineData("", false)]
    public void Step_header_is_detected_after_bom_and_whitespace(string text, bool expected) =>
        Assert.Equal(expected, IfcObjectDigest.IsStep(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task Worker_issue_ifc_global_ids_must_be_ifc_ids()
    {
        var calls = 0;
        var gate = ResetProxy.For<IProcessingWorkerGate>((_, _) => { calls++; return Task.FromResult(Fire3D.Application.Authentication.AuthResult<JsonElement>.Ok(JsonSerializer.SerializeToElement(new { code = "OK" }))); });
        var controller = new Fire3D.API.Controllers.ProcessingWorkerController(gate, Microsoft.Extensions.Options.Options.Create(new ProcessingWorkerOptions()))
            { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };
        object Input(object ids) => JsonSerializer.SerializeToElement(new
        {
            attemptId = Guid.NewGuid(), leaseToken = Guid.NewGuid(),
            output = new { artifactType = "preview_glb", objectKey = "worker-output/x/p.glb", schemaVersion = "1", validatorVersion = "fake", sha256Hash = new string('a', 64), sizeBytes = 10L, outcome = "Passed",
                metadata = new { objectAnchors = new[] { "door-1" } }, issues = new[] { new { severity = "Warning", code = "IFC_EXIT_UNKNOWN", message = "No exit found.", ifcGlobalIds = ids } } }
        });
        var bad = await controller.Output(Guid.NewGuid(), (JsonElement)Input(new[] { "door-1" }), default);
        Assert.Equal(400, Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(bad).StatusCode);
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Output(Guid.NewGuid(), (JsonElement)Input(new[] { "1hOSvn6df7F8_7GcBWlRGQ" }), default));
        Assert.Equal(1, calls);
    }
}
