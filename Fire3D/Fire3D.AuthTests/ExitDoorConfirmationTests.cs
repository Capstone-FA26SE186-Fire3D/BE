using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Fire3D.Infrastructure.Persistence;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // BE#100 option B: IFC_EXIT_UNKNOWN keeps an accepted revision in NeedsFix until an IfcDoor is confirmed as door.exit.
    [PostgresFact]
    public async Task Exit_unknown_waits_in_needs_fix_until_an_ifc_door_is_confirmed_as_door_exit()
    {
        const string door = "3Fx9Qd2bT0Hh8Fv5tW3pAa";
        var revision = await SeedVerifiedIfcRevision();
        var metadata = BuildingMetadataValidatorTests.Fixture();
        metadata["interactables"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "ifc:" + door, ["ifcGlobalId"] = door, ["catalogKey"] = "door.standard", ["source"] = "ifc", ["ifcClass"] = "IfcDoor",
            ["confidence"] = 0.95, ["position"] = new JsonArray(1, 0, 2)
        });
        await SeedEquipmentGeometry(revision, metadata, [new { severity = "Warning", code = "IFC_EXIT_UNKNOWN", message = "No external exit door found." }]);
        Assert.Equal("NeedsFix", (string?)await ScalarAsync($"SELECT status::text FROM revisions WHERE id='{revision}'"));

        await SeedEditorCatalog();
        await ExecuteAsync("""
            UPDATE runtime_compatibility_catalog SET capabilities=capabilities||'["door.exit","door.standard"]'::jsonb,
             capability_contracts=capability_contracts||'{"door.exit":{"version":"1.0.0","objectKinds":["Equipment"]},"door.standard":{"version":"1.0.0","objectKinds":["Equipment"]}}'::jsonb
             WHERE is_active;
            """);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var view = (await client.GetFromJsonAsync<JsonObject>($"/api/revisions/{revision}/equipment-candidates"))!;
        Assert.Equal(("NeedsFix", true), (view["revisionStatus"]!.GetValue<string>(), view["exitConfirmationRequired"]!.GetValue<bool>()));
        var extinguisher = view["items"]![0]!["id"]!.GetValue<string>();

        async Task<JsonObject> Decide(long version, string candidate, string decision, string? catalogKey)
        {
            var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/revisions/{revision}/equipment-decisions")
                { Content = JsonContent.Create(new { items = new[] { new { candidateId = candidate, decision, catalogKey } } }) };
            patch.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
            var response = await client.SendAsync(patch);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        }

        // A non-door candidate retyped as door.exit is not an exit door.
        var notDoor = await Decide(0, extinguisher, "Retyped", "door.exit");
        Assert.Equal(("NeedsFix", true), (notDoor["revisionStatus"]!.GetValue<string>(), notDoor["exitConfirmationRequired"]!.GetValue<bool>()));
        // The IfcDoor retyped as door.exit resolves it without a new upload.
        var ready = await Decide(1, "ifc:" + door, "Retyped", "door.exit");
        Assert.Equal(("ReadyForScenario", false), (ready["revisionStatus"]!.GetValue<string>(), ready["exitConfirmationRequired"]!.GetValue<bool>()));
        Assert.Equal("ReadyForScenario", (string?)await ScalarAsync($"SELECT status::text FROM revisions WHERE id='{revision}'"));
        // Rejecting the only exit door sends the revision back.
        var back = await Decide(2, "ifc:" + door, "Rejected", null);
        Assert.Equal(("NeedsFix", true), (back["revisionStatus"]!.GetValue<string>(), back["exitConfirmationRequired"]!.GetValue<bool>()));
        Assert.Equal("NeedsFix", (string?)await ScalarAsync(
            $"SELECT new_values->>'revisionStatus' FROM audit_logs WHERE target_entity='RevisionEquipmentDecisions' AND target_id='{revision}' ORDER BY created_at DESC LIMIT 1"));
    }

    // Supabase client roles (anon, authenticated) get nothing in public: grants left by Supabase defaults are removed.
    [PostgresFact]
    public async Task Client_roles_have_no_privileges_on_public_relations()
    {
        await ExecuteAsync("GRANT ALL ON scenario_drafts TO anon; GRANT ALL ON registration_email_challenges TO authenticated");
        using var stream = typeof(Fire3DDbContext).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.RevokeClientRoleGrants.sql")!;
        await ExecuteAsync(await new StreamReader(stream).ReadToEndAsync());
        Assert.Equal(0L, await ScalarAsync("""
            SELECT count(*) FROM pg_class c WHERE c.relnamespace='public'::regnamespace AND c.relkind IN('r','p','v','m','f','S')
             AND EXISTS(SELECT 1 FROM aclexplode(COALESCE(c.relacl,acldefault('r',c.relowner))) a WHERE a.grantee IN('anon'::regrole,'authenticated'::regrole))
            """));
    }
}
