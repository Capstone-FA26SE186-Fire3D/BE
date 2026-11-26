using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Ifc;
using Fire3D.Application.Scenarios.Commands.UpdateScenarioDraft;
using Fire3D.Tests.Shared;
using Xunit;

namespace Fire3D.AuthTests;

// Unit tests of the editor preview, draft-state normalization and building_metadata rules (no database, no S3).
public sealed class EditorPreviewBuilderUnitTests
{
    private sealed class FakeSigner(Func<string, long, SignedDownload?> sign) : IPreviewDownloadSigner
    {
        public int Calls;
        public Task<SignedDownload?> SignAsync(string storageKey, long expectedSizeBytes, CancellationToken ct) { Calls++; return Task.FromResult(sign(storageKey, expectedSizeBytes)); }
    }

    private static readonly string Hash = new('a', 64);
    private static readonly DateTimeOffset Expires = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    private static JsonElement ValidMetadata => JsonSerializer.SerializeToElement(EditorContractFixtures.Geometry());

    private static EditorPreviewSource Source(Guid? artifact, JsonElement? metadata, long? size = 1024, string? key = "revisions/r/preview.glb", string? hash = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "ReadyForScenario", artifact, Guid.NewGuid(), key, hash ?? Hash, metadata, size);

    [Fact]
    public async Task A_verified_artifact_with_valid_metadata_is_ready_with_a_signed_url_and_its_coordinates()
    {
        var signer = new FakeSigner((key, size) => new($"https://s3.test/{key}?size={size}", Expires));
        var result = await EditorPreviewBuilder.BuildAsync(Source(Guid.NewGuid(), ValidMetadata), signer, default);
        Assert.True(result.IsSuccess);
        var preview = result.Value!;
        Assert.Equal(("Ready", "https://s3.test/revisions/r/preview.glb?size=1024", (DateTimeOffset?)Expires), (preview.Status, preview.DownloadUrl, preview.ExpiresAt));
        Assert.Equal("fet3d.editor/1", preview.SchemaVersion);
        Assert.Equal(JsonValueKind.Array, preview.Floors!.Value.ValueKind);
        Assert.NotNull(preview.CoordinateTransform);
        Assert.Equal(1, signer.Calls);
    }

    [Fact]
    public async Task Without_an_artifact_or_a_verified_object_nothing_is_signed()
    {
        var signer = new FakeSigner((_, _) => throw new InvalidOperationException("must not sign"));
        var none = (await EditorPreviewBuilder.BuildAsync(Source(null, null), signer, default)).Value!;
        Assert.Equal(("NotReady", (string?)null, (JsonElement?)null), (none.Status, none.DownloadUrl, none.Floors));
        // Valid metadata but no size, no key or a hash that is not hex: coordinates are served, the GLB is not.
        foreach (var source in new[] { Source(Guid.NewGuid(), ValidMetadata, size: null), Source(Guid.NewGuid(), ValidMetadata, key: " "), Source(Guid.NewGuid(), ValidMetadata, hash: new string('z', 64)) })
        {
            var preview = (await EditorPreviewBuilder.BuildAsync(source, signer, default)).Value!;
            Assert.Equal("NotReady", preview.Status);
            Assert.NotNull(preview.Floors);
        }
        Assert.Equal(0, signer.Calls);
        // The object is not in storage yet.
        var missing = new FakeSigner((_, _) => null);
        Assert.Equal("NotReady", (await EditorPreviewBuilder.BuildAsync(Source(Guid.NewGuid(), ValidMetadata), missing, default)).Value!.Status);
    }

    [Fact]
    public async Task Legacy_or_invalid_metadata_needs_reprocessing_and_hides_coordinates()
    {
        var signer = new FakeSigner((_, _) => new("https://s3.test/x", Expires));
        var legacy = JsonSerializer.SerializeToElement(new { floors = new[] { new { id = "L1" } } });
        var preview = (await EditorPreviewBuilder.BuildAsync(Source(Guid.NewGuid(), legacy), signer, default)).Value!;
        Assert.Equal(("ReprocessRequired", (JsonElement?)null, (string?)null), (preview.Status, preview.Floors, preview.SchemaVersion));
        Assert.Equal(0, signer.Calls);
        var broken = EditorContractFixtures.Geometry().AsObject(); broken["floors"] = "L1";
        Assert.Equal("ReprocessRequired", EditorPreviewMetadata.Classify(Source(Guid.NewGuid(), JsonSerializer.SerializeToElement(broken))));
        Assert.Equal("NotReady", EditorPreviewMetadata.Classify(Source(null, ValidMetadata)));
        Assert.Equal("Valid", EditorPreviewMetadata.Classify(Source(Guid.NewGuid(), ValidMetadata)));
        Assert.Null(EditorPreviewMetadata.Field(Source(Guid.NewGuid(), null), "floors"));
    }

    [Fact]
    public async Task Storage_down_is_a_503_not_a_broken_preview()
    {
        var signer = new FakeSigner((_, _) => throw new PreviewStorageUnavailableException(new IOException("s3 down")));
        var result = await EditorPreviewBuilder.BuildAsync(Source(Guid.NewGuid(), ValidMetadata), signer, default);
        Assert.Equal(("PREVIEW_STORAGE_UNAVAILABLE", 503), (result.Error?.Code, result.Error?.Status));
    }
}

public sealed class DraftStateInputUnitTests
{
    [Fact]
    public void A_v1_document_is_shape_checked_and_stored_as_sent()
    {
        var state = EditorContractFixtures.State();
        var ok = DraftStateInput.Normalize(state);
        Assert.True(ok.IsSuccess);
        Assert.Same(state, ok.Value);

        state["objects"]![0]!.AsObject()["color"] = "red";
        var invalid = DraftStateInput.Normalize(state);
        Assert.Equal(("EDITOR_SCHEMA_INVALID", 422), (invalid.Error?.Code, invalid.Error?.Status));
        Assert.Contains(invalid.Error!.Issues!, x => x.Path == "$.objects[0].color");
    }

    [Fact]
    public void Unsupported_versions_and_non_objects_are_refused_never_migrated()
    {
        Assert.Equal(("EDITOR_SCHEMA_VERSION_UNSUPPORTED", 422), (DraftStateInput.Normalize(new JsonObject { ["schemaVersion"] = "fet3d.editor/9" }).Error?.Code, 422));
        Assert.Equal(("VALIDATION_ERROR", 400), (DraftStateInput.Normalize(new JsonArray()).Error?.Code, DraftStateInput.Normalize(null).Error!.Status));
        Assert.Contains("fet3d.editor/1", DraftStateInput.Unsupported<int>().Error!.Message);
    }

    [Fact]
    public void A_legacy_draft_keeps_its_typed_projection()
    {
        var legacy = DraftStateInput.Normalize(new JsonObject { ["learnerInstructions"] = "Thoát theo cầu thang bộ" });
        Assert.True(legacy.IsSuccess);
        Assert.Equal("Thoát theo cầu thang bộ", legacy.Value!["learnerInstructions"]!.GetValue<string>());
        Assert.Equal(("VALIDATION_ERROR", 400), (DraftStateInput.Normalize(new JsonObject { ["spawnPoints"] = "first floor" }).Error?.Code, 400));
    }
}

public sealed class BuildingMetadataValidatorUnitTests
{
    // Parsed from text like the worker's artifact (numbers read from JSON, integers included).
    private static JsonObject Candidate(string id = "c1") => JsonNode.Parse($$"""
        {"id":"{{id}}","ifcGlobalId":"1hOSvn6df7F8_7GcBWlRGQ","catalogKey":"extinguisher.portable","source":"ifc",
         "ifcClass":"IfcFireSuppressionTerminal","confidence":0.9,"floorId":"L1","position":[1.5,0,2],"rotationY":90,"bbox":[0.3,0.3,0.6]}
        """)!.AsObject();

    private static JsonObject Metadata(params JsonObject[] interactables) => new()
    {
        ["schemaVersion"] = BuildingMetadataValidator.SchemaVersion,
        ["spaces"] = new JsonArray(new JsonObject { ["ifcGlobalId"] = "0aOSvn6df7F8_7GcBWlRGQ" }),
        ["interactables"] = new JsonArray(interactables.Select(x => (JsonNode)x).ToArray())
    };

    [Fact]
    public void A_complete_candidate_list_is_valid()
    {
        Assert.Empty(BuildingMetadataValidator.Validate(Metadata(Candidate("c1"), Candidate("c2"))));
        Assert.Empty(BuildingMetadataValidator.Validate(new JsonObject { ["schemaVersion"] = "fet3d.building/1" }));
        Assert.Equal("fet3d.building/1", BuildingMetadataValidator.DeclaredVersion(Metadata()));
        Assert.Null(BuildingMetadataValidator.DeclaredVersion(new JsonArray()));
    }

    [Fact]
    public void Root_fields_have_a_version_known_keys_and_bounded_object_arrays()
    {
        Assert.Equal("$", Assert.Single(BuildingMetadataValidator.Validate(new JsonArray())).Path);
        var root = Metadata();
        root["schemaVersion"] = "fet3d.building/2";
        root["extra"] = 1;
        root["sources"] = null;
        root["materials"] = new JsonArray();
        root["spaces"]!.AsArray().Add(new JsonObject { ["ifcGlobalId"] = "0aOSvn6df7F8_7GcBWlRGQ" });
        var paths = BuildingMetadataValidator.Validate(root).Select(x => x.Path).ToArray();
        Assert.Contains("$.schemaVersion", paths);
        Assert.Contains("$.extra", paths);
        Assert.Contains("$.sources", paths);
        Assert.Contains("$.materials", paths);
        Assert.Contains("$.spaces[1].ifcGlobalId", paths);
    }

    [Fact]
    public void Each_candidate_field_is_checked()
    {
        var bad = Candidate("c1");
        bad["ifcGlobalId"] = "short"; bad["catalogKey"] = "Water Sink"; bad["source"] = "guess"; bad["confidence"] = 1.5;
        bad["position"] = new JsonArray(1, 2); bad["rotationY"] = 400; bad["bbox"] = new JsonArray(0.3, 0, 0.6); bad["floorId"] = ""; bad["owner"] = "x";
        var duplicate = Candidate("c1");
        var paths = BuildingMetadataValidator.Validate(Metadata(bad, duplicate)).Select(x => x.Path).ToArray();
        foreach (var field in new[] { "ifcGlobalId", "catalogKey", "source", "confidence", "position", "rotationY", "bbox", "floorId", "owner" })
            Assert.Contains($"$.interactables[0].{field}", paths);
        Assert.Contains("$.interactables[1].id", paths);
        var huge = Metadata();
        huge["interactables"] = new JsonArray(Enumerable.Range(0, 5001).Select(i => (JsonNode)Candidate($"c{i}")).ToArray());
        Assert.Equal("$.interactables", Assert.Single(BuildingMetadataValidator.Validate(huge)).Path);
    }
}
