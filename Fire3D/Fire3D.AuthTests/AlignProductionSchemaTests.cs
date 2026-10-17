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
}
