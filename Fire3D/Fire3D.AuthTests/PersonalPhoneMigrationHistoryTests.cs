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
    public async Task Personal_phone_real_history_upgrade_preserves_accounts_or_rolls_back_duplicate_data()
    {
        foreach (var duplicate in new[] { false, true })
        {
            var name = "fet3d_personal_phone_" + Guid.NewGuid().ToString("N");
            await using var admin = new NpgsqlConnection(adminConnection); await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin).ExecuteNonQueryAsync();
            try
            {
                var source = new NpgsqlConnectionStringBuilder(testConnection) { Database = name }.ConnectionString;
                await using var db = new Fire3DDbContext(new DbContextOptionsBuilder<Fire3DDbContext>().UseNpgsql(source).Options);
                var migrator = db.GetService<IMigrator>(); await migrator.MigrateAsync("20261007152000_AddProcessingRedisRecovery");
                await using var connection = new NpgsqlConnection(source); await connection.OpenAsync();
                using var seed = new NpgsqlCommand("""
                    INSERT INTO users(id,email,username,role,is_active,deleted_at,phone_number,profile_revision)
                    VALUES(gen_random_uuid(),'personal-a@example.test','personal_a','Trainee',false,now(),' (070) 636-4866 ',7),
                          (gen_random_uuid(),'personal-b@example.test','personal_b','Trainee',true,NULL,@phone,1),
                          (gen_random_uuid(),'personal-c@example.test','personal_c','Trainee',true,NULL,NULL,1);
                    """, connection);
                seed.Parameters.AddWithValue("phone", NpgsqlTypes.NpgsqlDbType.Text, duplicate ? "0706364866" : DBNull.Value);
                await seed.ExecuteNonQueryAsync();
                const string snapshot = "SELECT jsonb_agg(to_jsonb(u) ORDER BY id)::text FROM users u";
                var before = await new NpgsqlCommand(snapshot, connection).ExecuteScalarAsync();
                if (duplicate)
                { var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync()); Assert.Contains("Personal phone preflight failed", error.MessageText); }
                else await db.Database.MigrateAsync();
                Assert.Equal(before, await new NpgsqlCommand(snapshot, connection).ExecuteScalarAsync());
                Assert.Equal(!duplicate, await new NpgsqlCommand("SELECT to_regclass('public.users_phone_normalized_key') IS NOT NULL", connection).ExecuteScalarAsync());
                Assert.Equal(duplicate ? 0L : 1L, await new NpgsqlCommand("SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\"='20261007160000_AddPersonalPhoneUniqueness'", connection).ExecuteScalarAsync());
            }
            finally { NpgsqlConnection.ClearAllPools(); await new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin).ExecuteNonQueryAsync(); }
        }
    }
}
