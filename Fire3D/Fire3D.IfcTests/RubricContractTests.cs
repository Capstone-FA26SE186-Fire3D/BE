using System.Text.Json.Nodes;
using Fire3D.Application.Content;
using Fire3D.Application.Editor;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class RubricContractTests
{
    private static JsonObject Rubric(JsonNode? version)
    {
        var rubric=JsonNode.Parse("""{"pass_threshold":1,"criteria":[{"id":"exit","metric":"reached_exit","mandatory":true,"weight":1,"operator":"eq","threshold":1}]}""")!.AsObject();
        if(version is not null)rubric["schema_version"]=version;
        return rubric;
    }

    [Fact]
    public void Only_the_string_1_is_a_supported_schema_version()
    {
        Assert.Empty(ScenarioStateV1Validator.ValidateRubricDocument(Rubric("1")));
        foreach(var bad in new JsonNode[]{JsonValue.Create(1),"v1","1.0","2"})
            Assert.Contains(ScenarioStateV1Validator.ValidateRubricDocument(Rubric(bad)),x=>x.Code=="RUBRIC_SCHEMA_VERSION_UNSUPPORTED"&&x.Path=="$.rubric.schema_version");
        Assert.Contains(ScenarioStateV1Validator.ValidateRubricDocument(Rubric(null)),x=>x.Code=="FIELD_REQUIRED"&&x.Path=="$.rubric.schema_version");
    }

    [Fact]
    public void Library_editor_shape_and_legacy_snapshot_use_the_same_registry()
    {
        Assert.Contains(LibraryContent.Validate("RubricSample",new("Sample",new JsonObject{["rubric"]=Rubric("1.0")})),x=>x.Code=="RUBRIC_SCHEMA_VERSION_UNSUPPORTED"&&x.Path=="$.payload.rubric.schema_version");
        var state=Fire3D.Tests.Shared.EditorContractFixtures.State();
        state["rubric"]=Rubric("v1");
        Assert.Contains(ScenarioStateV1Validator.ValidateShape(state),x=>x.Code=="RUBRIC_SCHEMA_VERSION_UNSUPPORTED");
        JsonNode Legacy(string version)=>JsonNode.Parse("""{"spawnPoints":[{"x":1,"y":2,"z":3,"rotation":0}],"hazards":[],"scoringConfig":{"baseScore":100,"timeLimitSeconds":60,"penaltyPerMistake":5},"routingConfig":{"evacuationRoutes":["exit-a"]},"learningObjectives":["Evacuate"],"learnerInstructions":"Follow exits","rubric":{"schema_version":"VERSION","pass_threshold":1,"criteria":[{"id":"exit","metric":"exit","mandatory":true,"weight":1,"operator":"gte","threshold":1}]}}""".Replace("VERSION",version))!;
        Assert.Contains(ScenarioDraftStructuralValidator.Validate(Legacy("1.0")),x=>x.Code=="RUBRIC_SCHEMA_VERSION_UNSUPPORTED");
        Assert.Empty(ScenarioDraftStructuralValidator.Validate(Legacy("1")));
    }

    [Fact]
    public void Catalog_publishes_versions_metrics_operators_and_limits_without_defaults_for_thresholds()
    {
        var catalog=RubricContract.Catalog;
        Assert.Equal(["1"],catalog.SupportedSchemaVersions);Assert.Equal("1",catalog.DefaultSchemaVersion);
        Assert.Equal(EditorContract.LearnerMetrics,catalog.Metrics.Select(x=>x.Id).ToArray());
        Assert.Equal(["gte","lte","eq"],catalog.Operators);Assert.Equal(100,catalog.MaxCriteria);
        Assert.All(catalog.Metrics,x=>Assert.False(string.IsNullOrWhiteSpace(x.Description)));
    }
}
