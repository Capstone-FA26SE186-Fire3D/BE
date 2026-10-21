using System.Text.Json.Nodes;
using Fire3D.Application.Ifc;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class IfcRuleBranchTests
{
    [Fact]
    public void Style_preset_rejects_missing_building_types_long_keywords_and_a_malformed_default_scheme()
    {
        var issues = new List<ScenarioDraftValidationIssue>();
        StyleRules.ValidatePreset(JsonNode.Parse("""
            {"buildingTypes":[],"keywords":["lobby","LONG"],"defaultScheme":"Not a code!",
             "materials":{"default":{"floor":"floor.tile","wall":"wall.paint","ceiling":"ceiling.panel"}}}
            """.Replace("LONG", new string('k', 51)))!.AsObject(), issues);
        Assert.Equal(["$.payload.buildingTypes", "$.payload.defaultScheme", "$.payload.keywords"], issues.Where(x => x.Code == "FIELD_INVALID").Select(x => x.Path).Order());
    }

    [Fact]
    public void Equipment_decisions_refuse_a_catalog_key_on_rejection_or_in_the_wrong_format()
    {
        var issues = EquipmentDecisionRules.Validate(new([
            new("cand-1", "Rejected", "extinguisher.portable"),
            new("cand-2", "Retyped", "Fire Hose"),
            new("cand-3", "Retyped", "hose.reel")]));
        Assert.Equal(["VALUE_UNSUPPORTED@$.items[0].catalogKey", "VALUE_UNSUPPORTED@$.items[1].catalogKey"], issues.Select(x => x.Code + "@" + x.Path));
    }
}
