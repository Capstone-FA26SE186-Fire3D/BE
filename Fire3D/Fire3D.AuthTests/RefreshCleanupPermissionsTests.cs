using Fire3D.Infrastructure.Authentication;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class RefreshCleanupPermissionsTests
{
    [BillingPostgresFact]
    public async Task Restricted_api_can_cleanup_expired_families_without_direct_delete()
    {
        await using var db=await BillingDatabase.Create();
        await db.Sql("DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN CREATE ROLE fire3d_api NOLOGIN; END IF; END $$; GRANT USAGE ON SCHEMA public TO fire3d_api; GRANT SELECT ON auth_refresh_tokens TO fire3d_api; REVOKE DELETE ON auth_refresh_tokens FROM fire3d_api;");
        var expired=Guid.NewGuid();var mixed=Guid.NewGuid();
        await db.Sql($$"""
            INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at,consumed_at) VALUES
             (gen_random_uuid(),'{{BillingDatabase.Owner}}','{{expired}}','expired',now()-interval '20 days',now()-interval '8 days',now()-interval '19 days'),
             (gen_random_uuid(),'{{BillingDatabase.Owner}}','{{mixed}}','mixed-old',now()-interval '20 days',now()-interval '8 days',now()-interval '19 days'),
             (gen_random_uuid(),'{{BillingDatabase.Owner}}','{{mixed}}','mixed-active',now(),now()+interval '1 day',NULL);
            """);
        await BackendDatabasePermissionsTests.Apply(db,"AddRefreshCleanupGate");
        await using var context=db.Context();
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("SET ROLE fire3d_api");
        Assert.Equal(1,await new RefreshTokenCleanupStore(context).DeleteExpiredFamiliesAsync(7,20,default));
        Assert.Equal(false,await db.Scalar("SELECT has_table_privilege('fire3d_api','auth_refresh_tokens','DELETE')"));
        Assert.Equal(2L,await db.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE family_id='{mixed}'"));
        Assert.Equal(0L,await db.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE family_id='{expired}'"));
        Assert.Equal(false,await db.Scalar("SELECT has_function_privilege('anon','public.cleanup_expired_refresh_family(uuid,uuid,integer)','EXECUTE')"));
        await context.Database.ExecuteSqlRawAsync("RESET ROLE");
    }
}
