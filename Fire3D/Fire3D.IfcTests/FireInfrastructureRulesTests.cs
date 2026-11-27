using System.Text.Json.Nodes;
using Fire3D.Application.FireSafety;
using Fire3D.Application.Ifc;
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

    // BE#224: a locked exit door and a wedged fire door are door states of one situation, refused in the infrastructure.
    [Theory]
    [InlineData("door.exit_locked")]
    [InlineData("door.fire_wedged")]
    public void Door_states_are_scenario_items_not_infrastructure(string capability)
    {
        var door = Item("ext-1", "Equipment");
        door["capability"]!["id"] = capability;
        var issues = FireInfrastructureRules.Validate(null, null, new JsonArray(door), complete: false, null);
        Assert.Contains(issues, x => x is { Code: "INFRASTRUCTURE_CAPABILITY_NOT_FIXED", Path: "$.items[0].capability.id" });
        Assert.Contains(capability, FireInfrastructureRules.ScenarioItemCapabilities);
    }

    // BE#239: the first version names nothing after an IFC class and turns each confirmed exit door into an Exit Goal.
    private static EquipmentCandidateView Candidate(string id, string ifcClass, string key, string decision, string? retype = null, string globalId = "1hOSvn6df7F8_7GcBWlRGQ") =>
        new(id, globalId, key, "Rule", ifcClass, 0.95m, "L1", [1.5m, 0m, 2m], 90m, null, false, new(decision, retype, null, Guid.NewGuid(), DateTime.UtcNow), null);

    private static IReadOnlyDictionary<string, Fire3D.Application.Editor.CapabilityContract> Batch1()
    {
        var c = EditorContractFixtures.Load("fixtures/runtime-catalog.batch1.json").AsObject();
        return Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog.RuntimeCatalogContracts.For(new(c["runtimeVersion"]!.GetValue<string>(),
            c["protocolVersion"]!.GetValue<string>(), c["manifestSchemaVersion"]!.GetValue<string>(), c["capabilities"]!.DeepClone(), c["capabilityContracts"]!.DeepClone().AsObject()));
    }

    [Fact]
    public void First_version_has_no_ifc_class_labels_and_an_exit_goal_per_confirmed_exit_door()
    {
        var items = FireInfrastructureRules.SeedItems([
            Candidate("c1", "IfcBuildingElementProxy", "extinguisher.portable", "Confirmed", globalId: "2hOSvn6df7F8_7GcBWlRGQ"),
            Candidate("c2", "IfcBuildingElementProxy", "extinguisher.portable", "Confirmed", globalId: "3hOSvn6df7F8_7GcBWlRGQ"),
            Candidate("c3", "IfcDoor", "door.standard", "Retyped", "door.exit"),
            Candidate("c4", "IfcDoor", "door.exit", "Rejected", globalId: "4hOSvn6df7F8_7GcBWlRGQ"),
            Candidate("c5", "IfcSanitaryTerminal", "water.sink", "Confirmed", globalId: "5hOSvn6df7F8_7GcBWlRGQ")], Batch1());

        Assert.Equal(["infra-eq-1", "infra-eq-2", "infra-eq-3", "infra-exit-1", "infra-eq-4"], items.Select(x => x!["id"]!.GetValue<string>()));
        Assert.DoesNotContain(items, x => x!["label"]?.GetValue<string>().StartsWith("Ifc") == true);
        Assert.All(items.Where(x => x!["kind"]!.GetValue<string>() == "Equipment"), x => Assert.Null(x!["label"]));
        var exit = Assert.Single(items, x => x!["category"]!.GetValue<string>() == "Exit")!;
        var door = items.Single(x => x!["capability"]!["id"]!.GetValue<string>() == "door.exit")!;
        Assert.Equal(("Goal", "Lối ra ngoài", "exit.final", "1.0.0"), (exit["kind"]!.GetValue<string>(), exit["label"]!.GetValue<string>(),
            exit["capability"]!["id"]!.GetValue<string>(), exit["capability"]!["version"]!.GetValue<string>()));
        Assert.Equal(door["anchorId"]!.GetValue<string>(), exit["anchorId"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(door["placement"], exit["placement"]));
        Assert.Equal("WaterSource", items[4]!["category"]!.GetValue<string>());

        // The seeded items pass the save checks, and submit no longer asks for an exit.
        Assert.Empty(FireInfrastructureRules.Validate(null, null, items, complete: false, null));
        var submit = FireInfrastructureRules.Validate(Geometry(), "2.0.0", items, complete: true, Batch1());
        Assert.DoesNotContain(submit, x => x.Code is "EXIT_REQUIRED" or "INFRASTRUCTURE_KIND_MISMATCH");
    }

    [Fact]
    public void A_revision_without_a_confirmed_exit_door_still_needs_one()
    {
        var items = FireInfrastructureRules.SeedItems([Candidate("c1", "IfcBuildingElementProxy", "extinguisher.portable", "Confirmed")],
            new Dictionary<string, Fire3D.Application.Editor.CapabilityContract>());
        Assert.Single(items);
        Assert.Contains(FireInfrastructureRules.Validate(null, null, items, complete: true, null), x => x.Code == "EXIT_REQUIRED");
    }
}
