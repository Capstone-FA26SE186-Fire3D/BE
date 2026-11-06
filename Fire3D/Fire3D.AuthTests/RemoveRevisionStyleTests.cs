using System.Net;
using System.Net.Http.Json;
using Fire3D.Infrastructure.Content;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

// Issue #194: STYLE-01 is removed. The migration deletes StylePreset/ColorScheme items with their versions (production has two
// QA items), keeps every other Library item and the retention triggers, and drops the style overlay and its gate.
public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Remove_revision_style_migration_deletes_style_items_and_keeps_the_rest_of_the_library()
    {
        var legacyName = databaseName + "_style_removal";
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{legacyName}\"", admin).ExecuteNonQueryAsync();
        try
        {
            var legacyConnection = new NpgsqlConnectionStringBuilder(testConnection) { Database = legacyName }.ConnectionString;
            await using var db = new Fire3DDbContext(new DbContextOptionsBuilder<Fire3DDbContext>().UseNpgsql(legacyConnection).Options);
            await db.GetService<IMigrator>().MigrateAsync("20261031130000_AiSourcesPreparing");
            await using var connection = new NpgsqlConnection(legacyConnection);
            await connection.OpenAsync();
            async Task<object?> Scalar(string sql) => await new NpgsqlCommand(sql, connection).ExecuteScalarAsync();
            // Published and draft versions of each removed kind, next to an Equipment item that must survive.
            await new NpgsqlCommand("""
                INSERT INTO users(id,email,role,username,is_active,created_at,updated_at,password_hash)
                VALUES('00000000-0000-0000-0000-000000000194','style-admin@example.test','PlatformAdmin','style_admin',true,now(),now(),'fixture-hash');
                INSERT INTO organization_library_items(id,kind,code,created_by,created_at,updated_at) VALUES
                 ('10000000-0000-0000-0000-000000000001','StylePreset','QA-STYLE','00000000-0000-0000-0000-000000000194',now(),now()),
                 ('10000000-0000-0000-0000-000000000002','ColorScheme','QA-SCHEME','00000000-0000-0000-0000-000000000194',now(),now()),
                 ('10000000-0000-0000-0000-000000000003','Equipment','EXTINGUISHER','00000000-0000-0000-0000-000000000194',now(),now());
                INSERT INTO organization_library_versions(id,item_id,version_number,name,payload,payload_hash,status,published_by,published_at,created_by,created_at,updated_at)
                SELECT gen_random_uuid(),i.id,n,'v'||n,'{}',repeat('a',64),CASE WHEN n=1 THEN 'Published' ELSE 'Draft' END,
                       CASE WHEN n=1 THEN i.created_by END,CASE WHEN n=1 THEN now() END,i.created_by,now(),now()
                FROM organization_library_items i CROSS JOIN generate_series(1,2) n;
                """, connection).ExecuteNonQueryAsync();

            await db.Database.MigrateAsync();

            Assert.Equal(1L, await Scalar("SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='20261106100000_RemoveRevisionStyle'"));
            Assert.Equal("EXTINGUISHER:2", await Scalar("SELECT string_agg(i.code||':'||(SELECT count(*) FROM organization_library_versions v WHERE v.item_id=i.id),',') FROM organization_library_items i"));
            Assert.Equal(true, await Scalar("SELECT to_regclass('public.revision_style_selections') IS NULL AND to_regproc('public.revision_style_gate') IS NULL AND to_regproc('public.revision_style_library_ref') IS NULL AND to_regproc('public.immutable_revision_style_selection') IS NULL"));
            // The retention triggers are back on, and the kinds are gone from the CHECK.
            Assert.Equal("immutable_library_version:O,retain_library_item:O", await Scalar("SELECT string_agg(tgname||':'||tgenabled::text,',' ORDER BY tgname) FROM pg_trigger WHERE tgname IN('immutable_library_version','retain_library_item')"));
            var retained = await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand("DELETE FROM organization_library_items WHERE code='EXTINGUISHER'", connection).ExecuteNonQueryAsync());
            Assert.Contains("Retain library history", retained.MessageText);
            var check = await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand("INSERT INTO organization_library_items(id,kind,code,created_by,created_at,updated_at) VALUES(gen_random_uuid(),'StylePreset','NEW-STYLE','00000000-0000-0000-0000-000000000194',now(),now())", connection).ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, check.SqlState);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await new NpgsqlCommand($"DROP DATABASE \"{legacyName}\" WITH (FORCE)", admin).ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task Style_kinds_and_the_revision_style_api_are_gone()
    {
        var admin = await AdminFamily();
        await WithContentRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new ContentGates(db);
            foreach (var kind in new[] { "StylePreset", "ColorScheme" })
                Assert.Equal("VALIDATION_ERROR", (await gates.LibraryAsync("CreateItem", adminId, admin, Guid.Empty, new { kind, code = "QA-" + kind.ToUpperInvariant() }, "style-" + kind, null, default)).Error?.Code);
            var list = await gates.LibraryAsync("List", adminId, admin, Guid.Empty, new { kind = "StylePreset", admin = true }, null, null, default);
            Assert.Equal(0, list.Value.GetProperty("total").GetInt32());
        });

        using var client = factory!.CreateClient();
        var revision = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/revisions/{revision}/style")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/revisions/{revision}/style", new { presetVersionId = Guid.NewGuid() })).StatusCode);
    }
}
