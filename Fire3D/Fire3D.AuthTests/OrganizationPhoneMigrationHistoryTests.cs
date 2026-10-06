using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Phone_migration_upgrades_real_history_or_rolls_back_without_rewriting_legacy_data()
    {
        foreach (var duplicate in new[] { false, true })
        {
            var legacyName = databaseName + (duplicate ? "_phone_bad" : "_phone_clean");
            await using var admin = new NpgsqlConnection(adminConnection);
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{legacyName}\"", admin).ExecuteNonQueryAsync();
            try
            {
                var legacyConnection = new NpgsqlConnectionStringBuilder(testConnection) { Database = legacyName }.ConnectionString;
                await using var db = new Fire3DDbContext(new DbContextOptionsBuilder<Fire3DDbContext>().UseNpgsql(legacyConnection).Options);
                await db.GetService<IMigrator>().MigrateAsync("20261006090000_AddGoogleOnboardingDisplayName");
                await using var connection = new NpgsqlConnection(legacyConnection);
                await connection.OpenAsync();
                using var seed = new NpgsqlCommand("""
                    INSERT INTO organizations(id,name,slug,phone,profile_revision,is_active,deleted_at)
                    VALUES(gen_random_uuid(),'Legacy A','phone-a',' (070) 636-4866 ',7,false,now()),
                          (gen_random_uuid(),'Legacy B','phone-b',@phone,1,true,NULL);
                    """, connection);
                seed.Parameters.AddWithValue("phone", NpgsqlTypes.NpgsqlDbType.Text, duplicate ? "0706364866" : DBNull.Value);
                await seed.ExecuteNonQueryAsync();
                const string snapshot = "SELECT jsonb_agg(to_jsonb(o) ORDER BY id)::text FROM organizations o";
                var before = await new NpgsqlCommand(snapshot, connection).ExecuteScalarAsync();
                if (duplicate)
                {
                    var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync());
                    Assert.Contains("Organization phone preflight failed", error.MessageText);
                }
                else await db.Database.MigrateAsync();
                Assert.Equal(before, await new NpgsqlCommand(snapshot, connection).ExecuteScalarAsync());
                Assert.Equal(duplicate ? 0L : 1L, await new NpgsqlCommand("SELECT count(*) FROM public.\"__EFMigrationsHistory\" WHERE \"MigrationId\"='20261006110000_AddOrganizationPhoneUniqueness'", connection).ExecuteScalarAsync());
                Assert.Equal(!duplicate, await new NpgsqlCommand("SELECT to_regclass('public.organizations_phone_normalized_key') IS NOT NULL", connection).ExecuteScalarAsync());
            }
            finally
            {
                NpgsqlConnection.ClearAllPools();
                await new NpgsqlCommand($"DROP DATABASE \"{legacyName}\" WITH (FORCE)", admin).ExecuteNonQueryAsync();
            }
        }
    }
}
