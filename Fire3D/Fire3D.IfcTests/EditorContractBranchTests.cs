using System.Text.Json.Nodes;
using Fire3D.Application.Editor;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;
using Fire3D.Tests.Shared;
using Xunit;

namespace Fire3D.IfcTests;

/// <summary>Duplicate-ID, type and parameter-schema branches of the editor contract validators.</summary>
public sealed class EditorContractBranchTests
{
    private static IReadOnlyList<string> Issues(IEnumerable<ScenarioDraftValidationIssue> issues) => issues.Select(x => x.Code + "@" + x.Path).ToList();

    [Fact]
    public void Geometry_metadata_rejects_a_reused_floor_GlobalId_and_a_GLB_node_mapped_twice()
    {
        var geometry = EditorContractFixtures.Geometry();
        geometry["floors"]![1]!["ifcGlobalId"] = geometry["floors"]![0]!["ifcGlobalId"]!.GetValue<string>();
        geometry["semanticMapping"]![1]!["nodeId"] = "door_101";
        var issues = Issues(GeometryMetadataValidator.Validate(geometry));
        Assert.Equal(["ID_DUPLICATE@$.floors[1].ifcGlobalId", "ID_DUPLICATE@$.semanticMapping[1].nodeId"], issues);
    }

    [Fact]
    public void Capability_contract_with_a_malformed_parameter_schema_or_duplicate_action_is_not_published()
    {
        var contracts = JsonNode.Parse("""
            {"door.exit":{"version":"1.0.0","objectKinds":["Goal"],
              "parameters":{"type":"object","additionalProperties":"no",
                "properties":{"open":{"type":"boolean","minimum":0},"mode":{"type":"string","enum":[]},"size":{"type":"integer","maxLength":1.5}},
                "required":["open","ghost"]},
              "actions":[{"id":"open"},{"id":"open"}]}}
            """);
        var issues = new List<ScenarioDraftValidationIssue>();
        Assert.Empty(CapabilityContracts.Parse(JsonNode.Parse("""["door.exit"]"""), contracts, issues));
        Assert.Equal(new[]
        {
            "TYPE_INVALID@$.capabilityContracts.door.exit.parameters.additionalProperties",
            "VALUE_UNSUPPORTED@$.capabilityContracts.door.exit.parameters.properties.open",
            "FIELD_REQUIRED@$.capabilityContracts.door.exit.parameters.properties.mode.enum",
            "INTEGER_INVALID@$.capabilityContracts.door.exit.parameters.properties.size.maxLength",
            "FIELD_UNKNOWN@$.capabilityContracts.door.exit.parameters.required[1]",
            "ID_DUPLICATE@$.capabilityContracts.door.exit.actions[1].id"
        }.Order(), Issues(issues).Order());
    }

    [Fact]
    public void Scenario_state_requires_a_geometry_artifact_UUID_and_unique_rubric_criterion_ids()
    {
        var state = EditorContractFixtures.State();
        state["geometry"]!["artifactId"] = "preview-glb-latest";
        var criteria = state["rubric"]!["criteria"]!.AsArray();
        criteria.Add(criteria[0]!.DeepClone());
        var issues = Issues(ScenarioStateV1Validator.ValidateShape(state));
        Assert.Contains("STRING_FORMAT_INVALID@$.geometry.artifactId", issues);
        Assert.Contains($"ID_DUPLICATE@$.rubric.criteria[{criteria.Count - 1}].id", issues);
        Assert.Equal(2, issues.Count);
    }
}
