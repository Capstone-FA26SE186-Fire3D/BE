using System.Net;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // Production drift (17/10/2026): hand-made Docs v7 triggers made every API Building insert fail. The forward migration drops exactly
    // those triggers and grants USAGE on schema extensions; triggers the migrations create stay.
    [PostgresFact]
    public async Task Align_migration_removes_hand_made_triggers_and_keeps_migration_triggers()
    {
        await ExecuteAsync("""
            CREATE SCHEMA IF NOT EXISTS extensions;
            CREATE OR REPLACE FUNCTION record_core_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'legacy trigger still active'; END $$;
            CREATE TRIGGER core_audit AFTER INSERT ON buildings FOR EACH ROW EXECUTE FUNCTION record_core_audit();
            """);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var owner = await CreateOrganizationAsync("align-" + Guid.NewGuid().ToString("N")[..8]);
        await CreateAccountAsync("align-owner@example.test", UserRole.OrganizationUser, owner.Id);
        using var http = factory!.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync("align-owner@example.test")).AccessToken);
        Assert.Equal(HttpStatusCode.InternalServerError, (await PostBuilding(http, new { name = "Before", totalFloors = 1 })).StatusCode);

        var migrationTriggers = await ScalarAsync("SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid WHERE c.relnamespace='public'::regnamespace AND NOT t.tgisinternal AND t.tgname<>'core_audit'");
        using var stream = typeof(Fire3DDbContext).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.AlignProductionSchema.sql")!;
        await ExecuteAsync(await new StreamReader(stream).ReadToEndAsync());

        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM pg_trigger WHERE tgname='core_audit'"));
        Assert.Equal(migrationTriggers, await ScalarAsync("SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid WHERE c.relnamespace='public'::regnamespace AND NOT t.tgisinternal"));
        Assert.True((bool)(await ScalarAsync("SELECT has_schema_privilege('fire3d_api','extensions','USAGE')"))!);
        Assert.Equal(HttpStatusCode.Created, (await PostBuilding(http, new { name = "After", totalFloors = 1 })).StatusCode);
    }

    // Production drift (19/10/2026): Docs v7 CHECK constraints refused the first Geometry Complete. The forward migration drops only
    // constraints whose name and definition match the legacy ones, restores the current quotation checks, and keeps the rest.
    [PostgresFact]
    public async Task Drop_legacy_constraints_removes_only_matching_definitions()
    {
        await ExecuteAsync("""
            ALTER TABLE revision_artifacts ADD CONSTRAINT revision_artifacts_artifact_type_check CHECK (artifact_type = ANY (ARRAY['Preview'::text, 'Geometry'::text, 'Graph'::text, 'HazardGrid'::text, 'CandidatePackage'::text]));
            ALTER TABLE scenario_versions ADD CONSTRAINT scenario_versions_spawn_config_check CHECK (jsonb_typeof(spawn_config) = 'object'::text AND spawn_config ? 'nodeId'::text);
            ALTER TABLE quotations DROP CONSTRAINT check_quotation_issued;
            ALTER TABLE quotations ADD CONSTRAINT check_quotation_issued CHECK (status = 'Draft'::quotation_status_enum OR (issued_by IS NOT NULL AND issued_at IS NOT NULL));
            ALTER TABLE validation_runs ADD CONSTRAINT validation_runs_kind_check CHECK (kind = ANY (ARRAY['Geometry'::text, 'Scenario'::text, 'PlaytestPackage'::text]));
            ALTER TABLE users ADD CONSTRAINT users_email_check CHECK (email = lower(btrim(email)) AND POSITION(('@'::text) IN (email)) > 1);
            """);
        using var stream = typeof(Fire3DDbContext).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.DropLegacyConstraints.sql")!;
        var sql = await new StreamReader(stream).ReadToEndAsync();
        await ExecuteAsync(sql);
        await ExecuteAsync(sql);

        async Task<long> Count(string name) => (long)(await ScalarAsync($"SELECT count(*) FROM pg_constraint WHERE conname='{name}'"))!;
        Assert.Equal(0L, await Count("revision_artifacts_artifact_type_check"));
        Assert.Equal(0L, await Count("scenario_versions_spawn_config_check"));
        // Another definition under a listed name is not the legacy constraint; one only fixtures violate stays.
        Assert.Equal(1L, await Count("validation_runs_kind_check"));
        Assert.Equal(1L, await Count("users_email_check"));
        Assert.Contains("Cancelled", (string)(await ScalarAsync("SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname='check_quotation_issued'"))!);
        Assert.Equal(1L, await Count("check_quotation_amounts"));
    }
}
