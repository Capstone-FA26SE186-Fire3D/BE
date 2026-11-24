using System.Text.Json.Nodes;
using Fire3D.Application.FireSafety;
using Fire3D.Tests.Shared;
using Xunit;

namespace Fire3D.IfcTests;

// BE#213: infrastructure item rules (category vs kind, fixed capabilities, exit required) and issue paths under $.items.
public sealed class FireInfrastructureRulesTests
{
    private static JsonObject Item(string id, string category)
    {
        var o = EditorContractFixtures.State()["objects"]!.AsArray().Single(x => x!["id"]!.GetValue<string>() == id)!.DeepClone().AsObject();
        o["category"] = category;
        return o;
    }

    private static JsonObject Geometry() => new() { ["artifactId"] = Guid.NewGuid().ToString(), ["sha256Hash"] = new string('a', 64) };

    [Fact]
    public void Exit_and_equipment_items_pass_and_category_is_not_an_unknown_field()
    {
        var issues = FireInfrastructureRules.Validate(Geometry(), "1.4.0", new JsonArray(Item("goal-exit", "Exit"), Item("ext-1", "Equipment")), complete: true,
            Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog.RuntimeCatalogContracts.For(EditorContractFixtures.Catalog()));
        Assert.Empty(issues);
    }

    [Fact]
    public void Category_kind_and_fixed_capability_rules_report_item_paths()
    {
        var towel = Item("ext-1", "Equipment");
        towel["capability"]!["id"] = "item.towel";
        var issues = FireInfrastructureRules.Validate(null, null, new JsonArray(Item("ext-1", "Exit"), towel, Item("goal-exit", "Stair")), complete: false, null);
        Assert.Contains(issues, x => x is { Code: "INFRASTRUCTURE_KIND_MISMATCH", Path: "$.items[0].kind" });
        Assert.Contains(issues, x => x is { Code: "INFRASTRUCTURE_CAPABILITY_NOT_FIXED", Path: "$.items[1].capability.id" });
        Assert.Contains(issues, x => x is { Code: "FIELD_INVALID", Path: "$.items[2].category" });
        Assert.DoesNotContain(issues, x => x.Path.StartsWith("$.objects"));
    }

    [Fact]
    public void Submit_needs_an_exit_geometry_and_runtime()
    {
        var issues = FireInfrastructureRules.Validate(null, null, new JsonArray(Item("ext-1", "Equipment")), complete: true, null);
        Assert.Contains(issues, x => x.Code == "EXIT_REQUIRED");
        Assert.Contains(issues, x => x.Path == "$.geometry");
        Assert.Contains(issues, x => x.Path == "$.runtimeVersion");
        Assert.DoesNotContain(issues, x => x.Code is "SPAWN_REQUIRED" or "GOAL_REQUIRED");
        Assert.Single(FireInfrastructureRules.Validate(null, null, new JsonObject(), complete: false, null), x => x.Code == "FIELD_REQUIRED");
    }
}
