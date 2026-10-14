using Fire3D.Domain.Entities;
using Fire3D.Infrastructure.Authentication;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // Azure runs as a login that is only a member of fire3d_api; superuser test connections hid missing grants.
    [PostgresFact]
    public async Task Runtime_login_can_create_avatar_intents_and_run_payos_reads_but_not_write_money_tables()
    {
        var tenant = await SeedAiTenant();
        var package = Guid.NewGuid(); var quote = Guid.NewGuid();
        await ExecuteAsync($$"""
            INSERT INTO service_packages(id,code,name,unit_price,currency,duration_months,features,is_active,created_by,created_at,updated_at) VALUES('{{package}}','grant-{{package:N}}','Grant fixture',100,'VND',1,'{}',true,'{{adminId}}',now(),now());
            INSERT INTO quotations(id,organization_id,requested_by,quotation_number,quantity,unit_price,subtotal_amount,tax_amount,discount_amount,total_amount,currency,valid_until,created_at,updated_at) VALUES('{{quote}}','{{tenant.Org}}','{{tenant.User}}','grant-{{quote:N}}',1,100,100,0,0,100,'VND',now()+interval '1 day',now(),now());
            """);
        var login = "runtime_grants_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{RuntimeTestPassword}' IN ROLE fire3d_api");
        try
        {
            await using var db = BuildingContext(new NpgsqlConnectionStringBuilder(testConnection) { Username = login, Pooling = false }.ConnectionString);
            var store = new AvatarStore(db);
            var now = DateTime.UtcNow;
            var intent = new AvatarUploadIntent
            {
                Id = Guid.NewGuid(), UserId = tenant.User, StagingObjectKey = $"avatars/staging/{tenant.User:N}/{Guid.NewGuid():N}",
                ContentType = "image/png", ExpectedSizeBytes = 100, CreatedAt = now, ExpiresAt = now.AddMinutes(5)
            };
            await store.SaveUploadIntentAsync(intent, default);
            Assert.NotNull(await store.FindUploadIntentAsync(intent.Id, tenant.User, default));

            // PayOS recovery and billing reads see rows through RLS; the recovery claim can update operations.
            Assert.Equal(0, await db.Set<BillingCheckoutOperation>().CountAsync(x => x.Status == "Creating"));
            await db.Database.ExecuteSqlRawAsync("UPDATE billing_checkout_operations SET attempts=attempts WHERE false");
            await db.Database.ExecuteSqlRawAsync("UPDATE payment_provisioning_records SET status=status WHERE false");
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM quotations WHERE id='{quote}'").SingleAsync());
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM service_packages WHERE id='{package}'").SingleAsync());
            foreach (var table in new[] { "payos_payment_requests", "payment_transactions", "service_entitlements", "payos_webhook_inbox", "billing_command_receipts" })
                await db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM {table}").SingleAsync();

            // Money tables stay read-only for the general API identity.
            var denied = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM payment_transactions WHERE false"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            denied = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE payment_provisioning_records SET payment_transaction_id=payment_transaction_id WHERE false"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }
        finally { await ExecuteAsync($"DROP OWNED BY {login};DROP ROLE {login}"); }
    }

    // Supabase's hand-made baseline granted table-wide UPDATE; narrowing must keep the reviewed columns writable.
    [PostgresFact]
    public async Task Narrowing_replaces_table_wide_runtime_updates_with_reviewed_columns()
    {
        var local = "payos_local_" + Guid.NewGuid().ToString("N")[..20];
        await ExecuteAsync($"GRANT UPDATE ON users,organizations,auth_refresh_tokens TO fire3d_api;CREATE ROLE {local} NOLOGIN;GRANT SELECT,DELETE,TRUNCATE ON buildings TO {local}");
        try
        {
            await using (var stream = typeof(Fire3DDbContext).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.NarrowRuntimeWriteGrants.sql")!)
                await ExecuteAsync(await new StreamReader(stream).ReadToEndAsync());
            async Task<bool> Has(string check) => (bool)(await ScalarAsync("SELECT " + check))!;
            foreach (var (table, column) in new[] { ("users", "role"), ("users", "email"), ("users", "organization_id"), ("organizations", "slug"),
                ("organizations", "registration_owner_user_id"), ("auth_refresh_tokens", "token_hash"), ("auth_refresh_tokens", "user_id") })
                Assert.False(await Has($"has_column_privilege('fire3d_api','{table}','{column}','UPDATE')"), $"{table}.{column}");
            foreach (var (table, column) in new[] { ("users", "last_login_at"), ("users", "password_hash"), ("users", "avatar_storage_key"),
                ("organizations", "phone"), ("auth_refresh_tokens", "revoked_at"), ("auth_refresh_tokens", "consumed_at") })
                Assert.True(await Has($"has_column_privilege('fire3d_api','{table}','{column}','UPDATE')"), $"{table}.{column}");
            Assert.False(await Has($"has_table_privilege('{local}','buildings','DELETE')"));
            Assert.False(await Has($"has_table_privilege('{local}','buildings','TRUNCATE')"));
            Assert.True(await Has($"has_table_privilege('{local}','buildings','SELECT')"));
        }
        finally { await ExecuteAsync($"DROP OWNED BY {local};DROP ROLE {local}"); }
    }
}
