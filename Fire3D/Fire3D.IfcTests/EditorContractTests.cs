using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Editor;
using Fire3D.Application.Scenarios.Commands.UpdateScenarioDraft;
using Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog;
using Fire3D.Tests.Shared;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class EditorContractTests
{
    private static IReadOnlyDictionary<string, CapabilityContract> Contracts() => RuntimeCatalogContracts.For(EditorContractFixtures.Catalog());
    private static IReadOnlyList<string> Codes(IEnumerable<Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft.ScenarioDraftValidationIssue> issues, string? path = null) =>
        issues.Where(x => path is null || x.Path == path).Select(x => x.Code).ToList();

    [Fact]
    public void Shared_fixtures_satisfy_the_contract()
    {
        Assert.Empty(GeometryMetadataValidator.Validate(EditorContractFixtures.Geometry()));
        Assert.Empty(ScenarioStateV1Validator.ValidateShape(EditorContractFixtures.State()));
        Assert.Empty(ScenarioStateV1Validator.ValidateComplete(EditorContractFixtures.State(), Contracts()));
        var issues = new List<Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft.ScenarioDraftValidationIssue>();
        var catalog = EditorContractFixtures.Catalog();
        Assert.Equal(["equipment.extinguisher", "fire.source", "npc.evacuee"], CapabilityContracts.Parse(catalog.Capabilities, catalog.CapabilityContracts, issues).Keys.Order());
        Assert.Empty(issues);
    }

    [Fact]
    public void Transforms_map_sample_points_column_major()
    {
        var geometry = EditorContractFixtures.Geometry();
        var ifcToGlb = geometry["coordinateTransform"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
        // IFC millimetres, Z-up -> GLB metres, Y-up, right-handed; origin offset (-10, 0, 5).
        Assert.Equal((-9d, 3d, 3d), Round(EditorContract.Apply(ifcToGlb, 1000, 2000, 3000)));
        Assert.Equal((-10d, 0d, 5d), Round(EditorContract.Apply(ifcToGlb, 0, 0, 0)));
        var l2 = geometry["floors"]![1]!["transform"]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
        Assert.Equal((1d, 3.5d, 2d), Round(EditorContract.Apply(l2, 1, 0, 2)));
        static (double, double, double) Round((double X, double Y, double Z) p) => (Math.Round(p.X, 9), Math.Round(p.Y, 9), Math.Round(p.Z, 9));
    }

    [Fact]
    public void Geometry_rejects_unknown_fields_bad_units_matrices_and_dangling_floors()
    {
        var g = EditorContractFixtures.Geometry().AsObject();
        g["objectAnchors"] = new JsonArray("x");
        g["units"] = "mm";
        g["floors"]![0]!["transform"]![15] = 2;
        g["floors"]![1]!["id"] = "L1";
        g["semanticMapping"]![3]!["floorId"] = "L9";
        g["semanticMapping"]![1]!["ifcGlobalId"] = "1hOSvn6df7F8_7GcBWlRGQ";
        g["coordinateTransform"] = new JsonArray(Enumerable.Range(0, 16).Select(i => (JsonNode?)JsonValue.Create(i == 15 ? 1 : 0)).ToArray());
        var issues = GeometryMetadataValidator.Validate(g);
        Assert.Contains("FIELD_UNKNOWN", Codes(issues, "$.objectAnchors"));
        Assert.Contains("VALUE_UNSUPPORTED", Codes(issues, "$.units"));
        Assert.Contains("MATRIX_NOT_AFFINE", Codes(issues, "$.floors[0].transform"));
        Assert.Contains("MATRIX_SINGULAR", Codes(issues, "$.coordinateTransform"));
        Assert.Contains("ID_DUPLICATE", Codes(issues, "$.floors[1].id"));
        Assert.Contains("FLOOR_NOT_FOUND", Codes(issues, "$.semanticMapping[3].floorId"));
        Assert.Contains("ID_DUPLICATE", Codes(issues, "$.semanticMapping[1].ifcGlobalId"));
    }

    [Fact]
    public void Non_finite_numbers_and_unnormalized_quaternions_are_rejected()
    {
        var state = JsonNode.Parse(EditorContractFixtures.State().ToJsonString().Replace("\"x\": 2,", "\"x\": 1e400,").Replace("\"x\":2,", "\"x\":1e400,"))!;
        state["objects"]![1]!["placement"]!["rotation"]!["w"] = 0.5;
        var issues = ScenarioStateV1Validator.ValidateShape(state);
        Assert.Contains("NUMBER_INVALID", Codes(issues, "$.objects[0].placement.position.x"));
        Assert.Contains("QUATERNION_NOT_NORMALIZED", Codes(issues, "$.objects[1].placement.rotation"));
        var g = JsonNode.Parse(EditorContractFixtures.Geometry().ToJsonString().Replace("\"elevationMeters\":3.5", "\"elevationMeters\":-1e999"))!;
        Assert.Contains("NUMBER_INVALID", Codes(GeometryMetadataValidator.Validate(g), "$.floors[1].elevationMeters"));
    }

    [Fact]
    public void Put_shape_rejects_unknown_fields_duplicates_and_bad_enums_but_allows_partial_drafts()
    {
        Assert.Empty(ScenarioStateV1Validator.ValidateShape(JsonNode.Parse("""{"schemaVersion":"fet3d.editor/1","objects":[]}""")));
        var state = EditorContractFixtures.State();
        state["legacyField"] = 1;
        state["objects"]![2]!["placement"]!.AsObject()["extra"] = true;
        state["objects"]![3]!["id"] = "spawn-1";
        state["objects"]![4]!["kind"] = "Smoke";
        state["modePolicy"]!["allowedModes"] = new JsonArray("Learn", "Learn");
        var issues = ScenarioStateV1Validator.ValidateShape(state);
        Assert.Contains("FIELD_UNKNOWN", Codes(issues, "$.legacyField"));
        Assert.Contains("FIELD_UNKNOWN", Codes(issues, "$.objects[2].placement.extra"));
        Assert.Contains("ID_DUPLICATE", Codes(issues, "$.objects[3].id"));
        Assert.Contains("VALUE_UNSUPPORTED", Codes(issues, "$.objects[4].kind"));
        Assert.Contains("ID_DUPLICATE", Codes(issues, "$.modePolicy.allowedModes[1]"));
        var normalized = DraftStateInput.Normalize(state);
        Assert.Equal("EDITOR_SCHEMA_INVALID", normalized.Error?.Code);
        Assert.Equal(422, normalized.Error?.Status);
        Assert.Contains(normalized.Error!.Issues!, x => x.Path == "$.legacyField" && x.Message.Length > 0);
    }

    [Fact]
    public void Complete_validation_checks_capability_contract_parameters_and_required_objects()
    {
        var state = EditorContractFixtures.State();
        state["objects"]![2]!["parameters"]!["intensity"] = 99;
        state["objects"]![2]!["parameters"]!.AsObject()["color"] = "red";
        state["objects"]![3]!["parameters"]!["behaviour"] = "dance";
        state["objects"]![5]!["capability"]!["version"] = "9.9.9";
        var npcAsHazard = state["objects"]![3]!.DeepClone();
        npcAsHazard["id"] = "npc-as-hazard"; npcAsHazard["kind"] = "Hazard";
        state["objects"]!.AsArray().Add(npcAsHazard);
        state["objects"]!.AsArray().Add(JsonNode.Parse("""{"id":"bare-hazard","kind":"Hazard","parameters":{"intensity":1},"placement":{"floorId":"L1","position":{"x":0,"y":0,"z":0},"rotation":{"x":0,"y":0,"z":0,"w":1}}}"""));
        state["objects"]!.AsArray().Add(JsonNode.Parse("""{"id":"unknown-cap","kind":"Equipment","capability":{"id":"smoke.spread","version":"1.0.0"},"placement":{"floorId":"L1","position":{"x":0,"y":0,"z":0},"rotation":{"x":0,"y":0,"z":0,"w":1}}}"""));
        var issues = ScenarioStateV1Validator.ValidateComplete(state, Contracts());
        Assert.Contains("PARAMETER_OUT_OF_RANGE", Codes(issues, "$.objects[2].parameters.intensity"));
        Assert.Contains("FIELD_UNKNOWN", Codes(issues, "$.objects[2].parameters.color"));
        Assert.Contains("PARAMETER_NOT_ALLOWED", Codes(issues, "$.objects[3].parameters.behaviour"));
        Assert.Contains("CAPABILITY_VERSION_MISMATCH", Codes(issues, "$.objects[5].capability.version"));
        Assert.Contains("CAPABILITY_KIND_UNSUPPORTED", Codes(issues, "$.objects[6].capability.id"));
        Assert.Contains("CAPABILITY_REQUIRED", Codes(issues, "$.objects[7].capability"));
        Assert.Contains("PARAMETERS_NOT_ALLOWED", Codes(issues, "$.objects[7].parameters"));
        // smoke.spread is listed by the runtime but has no published contract, so editors cannot use it.
        Assert.Contains("CAPABILITY_NOT_SUPPORTED", Codes(issues, "$.objects[8].capability.id"));

        var missing = EditorContractFixtures.State();
        missing["objects"]![2]!["parameters"]!.AsObject().Remove("intensity");
        missing["objects"] = new JsonArray(missing["objects"]!.AsArray().Where(x => x!["kind"]!.GetValue<string>() is not ("Goal" or "BlockedElement")).Select(x => x!.DeepClone()).ToArray());
        missing.Remove("rubric"); missing.Remove("timeLimitSeconds");
        var gaps = ScenarioStateV1Validator.ValidateComplete(missing, Contracts());
        Assert.Contains("FIELD_REQUIRED", Codes(gaps, "$.objects[1].parameters.intensity"));
        Assert.Contains("GOAL_REQUIRED", Codes(gaps, "$.objects"));
        Assert.Contains("FIELD_REQUIRED", Codes(gaps, "$.rubric"));
        Assert.Contains("FIELD_REQUIRED", Codes(gaps, "$.timeLimitSeconds"));
        Assert.Empty(ScenarioStateV1Validator.ValidateShape(missing));
    }

    [Fact]
    public void Invalid_capability_contracts_are_not_offered()
    {
        var catalog = EditorContractFixtures.Catalog();
        var contracts = catalog.CapabilityContracts!.DeepClone().AsObject();
        contracts["fire.source"]!["parameters"]!["additionalProperties"] = true;
        contracts["npc.evacuee"]!["objectKinds"] = new JsonArray("Dragon");
        contracts["not.listed"] = JsonNode.Parse("""{"version":"1.0.0","objectKinds":["Hazard"]}""");
        var issues = new List<Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft.ScenarioDraftValidationIssue>();
        var parsed = CapabilityContracts.Parse(catalog.Capabilities, contracts, issues);
        Assert.Equal(["equipment.extinguisher"], parsed.Keys);
        Assert.Contains(issues, x => x.Code == "CAPABILITY_CONTRACT_INVALID" && x.Path.EndsWith("not.listed"));
        var view = RuntimeCatalogContracts.PublishedView(catalog with { CapabilityContracts = contracts });
        Assert.Equal(["equipment.extinguisher"], view.CapabilityContracts!.Select(x => x.Key));
        Assert.Empty(RuntimeCatalogContracts.For(catalog with { CapabilityContracts = null }));
    }

    [Theory]
    [InlineData("""{"spawnPoints":[],"hazards":[],"scoringConfig":{"baseScore":1,"timeLimitSeconds":1,"penaltyPerMistake":0},"routingConfig":{"evacuationRoutes":[]}}""", null)]
    [InlineData("""{"schemaVersion":"fet3d.editor/2","objects":[]}""", "EDITOR_SCHEMA_VERSION_UNSUPPORTED")]
    [InlineData("""{"schemaVersion":7}""", "EDITOR_SCHEMA_VERSION_UNSUPPORTED")]
    [InlineData("""{"spawnPoints":"bad"}""", "VALIDATION_ERROR")]
    [InlineData("""[]""", "VALIDATION_ERROR")]
    public void Normalize_distinguishes_legacy_unsupported_and_invalid(string json, string? code)
    {
        var result = DraftStateInput.Normalize(JsonNode.Parse(json));
        Assert.Equal(code, result.Error?.Code);
        if (code is null) Assert.Null(EditorContract.DeclaredVersion(result.Value));
    }

    [Fact]
    public void Versioned_state_round_trips_without_losing_supported_fields()
    {
        var state = EditorContractFixtures.State();
        var normalized = DraftStateInput.Normalize(state);
        Assert.True(normalized.IsSuccess);
        // jsonb may reorder keys; values must stay identical.
        Assert.True(JsonNode.DeepEquals(state, JsonNode.Parse(normalized.Value!.ToJsonString())));
        foreach (var kind in EditorContract.ObjectKinds)
            Assert.Contains(state["objects"]!.AsArray(), x => x!["kind"]!.GetValue<string>() == kind);
    }

    [Fact]
    public void Json_schema_documents_cover_every_fixture_field()
    {
        AssertCovered(EditorContractFixtures.Load("geometry-metadata.schema.json"), EditorContractFixtures.Geometry());
        AssertCovered(EditorContractFixtures.Load("scenario-state.schema.json"), EditorContractFixtures.State());
        var capability = EditorContractFixtures.Load("capability-contract.schema.json");
        foreach (var (_, contract) in EditorContractFixtures.CatalogFixture()["capabilityContracts"]!.AsObject()) AssertCovered(capability, contract!);
    }

    // Follows properties/items/$ref and fails on any fixture property the schema does not declare.
    private static void AssertCovered(JsonNode schemaDocument, JsonNode value, JsonNode? schema = null, string path = "$")
    {
        schema ??= schemaDocument;
        if (schema["$ref"] is JsonValue reference)
            schema = schemaDocument["$defs"]![reference.GetValue<string>().Split('/').Last()]!;
        if (value is JsonObject o)
        {
            var properties = schema["properties"] as JsonObject;
            var additional = schema["additionalProperties"];
            foreach (var (name, child) in o)
            {
                var childSchema = properties?[name] ?? additional as JsonObject;
                if (childSchema is null)
                {
                    // Only a free-form object (no declared properties, not closed) may carry undeclared members.
                    Assert.True(properties is null && additional is not JsonValue, $"{path}.{name} is not declared by the JSON Schema.");
                    continue;
                }
                if (child is not null) AssertCovered(schemaDocument, child, childSchema, $"{path}.{name}");
            }
        }
        else if (value is JsonArray a && schema["items"] is JsonObject items)
            for (var i = 0; i < a.Count; i++) if (a[i] is not null) AssertCovered(schemaDocument, a[i]!, items, $"{path}[{i}]");
    }
}
