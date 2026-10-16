using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Ifc;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // A repeated space GlobalId reaches the gate only if a worker bypasses the API check; Complete must still succeed (no endless retry).
    [PostgresFact]
    public async Task Repeated_space_global_ids_still_complete_with_one_fact_per_property()
    {
        var revision = await SeedVerifiedIfcRevision();
        var metadata = BuildingMetadataValidatorTests.Fixture();
        var first = metadata["spaces"]![0]!.DeepClone().AsObject();
        first["longName"] = "Renamed copy";
        metadata["spaces"]!.AsArray().Add(first);
        await SeedEquipmentGeometry(revision, metadata);
        Assert.Equal("ReadyForScenario", (string?)await ScalarAsync($"SELECT status::text FROM revisions WHERE id='{revision}'"));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM bim_facts WHERE revision_id='{revision}' AND ifc_global_id='{first["ifcGlobalId"]}' AND property_path='LongName'"));
    }

    // interactables: null (stored before the API check existed, or by a bypassing worker) reads as no candidates instead of failing.
    [PostgresFact]
    public async Task Null_interactables_read_as_no_candidates()
    {
        var revision = await SeedVerifiedIfcRevision();
        var metadata = BuildingMetadataValidatorTests.Fixture();
        metadata["interactables"] = null;
        await SeedEquipmentGeometry(revision, metadata);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var view = (await client.GetFromJsonAsync<JsonObject>($"/api/revisions/{revision}/equipment-candidates"))!;
        Assert.Empty(view["items"]!.AsArray());
    }

    // Only string GlobalIds are returned; anything else a worker put in details.ifcGlobalIds is dropped instead of failing the page.
    [PostgresFact]
    public async Task Issue_ifc_global_ids_keep_only_strings()
    {
        var f = await FinalReviewGeometry("Failed", [new { severity = "Error", code = "IFC_NO_SPACES", message = "No IfcSpace.",
            details = new { ifcGlobalIds = new object[] { 123, "1hOSvn6df7F8_7GcBWlRGQ", new { id = 1 } } } }]);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var issue = (await client.GetFromJsonAsync<JsonObject>($"/api/revisions/{f.Revision}/issues"))!["items"]![0]!;
        Assert.Equal(["1hOSvn6df7F8_7GcBWlRGQ"], issue["ifcGlobalIds"]!.AsArray().Select(x => x!.GetValue<string>()));
    }
}

public sealed class IfcReviewFixUnitTests
{
    private static List<string> Codes(JsonObject metadata) => BuildingMetadataValidator.Validate(metadata).Select(x => $"{x.Code} {x.Path}").ToList();

    [Fact]
    public void Null_containers_repeated_spaces_and_out_of_range_coordinates_are_rejected()
    {
        var nulls = BuildingMetadataValidatorTests.Fixture();
        nulls["interactables"] = null; nulls["spaces"] = null; nulls["materials"] = null;
        Assert.Equal(["FIELD_INVALID $.spaces", "FIELD_INVALID $.materials", "FIELD_INVALID $.interactables"], Codes(nulls));

        var repeated = BuildingMetadataValidatorTests.Fixture();
        repeated["spaces"]!.AsArray().Add(repeated["spaces"]![0]!.DeepClone());
        Assert.Contains($"ID_DUPLICATE $.spaces[{repeated["spaces"]!.AsArray().Count - 1}].ifcGlobalId", Codes(repeated));

        var far = BuildingMetadataValidatorTests.Fixture();
        far["interactables"]![0]!["position"] = new JsonArray(1e30, 0, 0);
        far["interactables"]![0]!["rotationY"] = 720;
        Assert.Equal(["FIELD_INVALID $.interactables[0].position", "FIELD_INVALID $.interactables[0].rotationY"], Codes(far));

        var newline = BuildingMetadataValidatorTests.Fixture();
        newline["interactables"]![0]!["ifcGlobalId"] = newline["interactables"]![0]!["ifcGlobalId"]!.GetValue<string>() + "\n";
        Assert.Equal(["FIELD_INVALID $.interactables[0].ifcGlobalId"], Codes(newline));
        Assert.Empty(Codes(BuildingMetadataValidatorTests.Fixture()));
    }
}
