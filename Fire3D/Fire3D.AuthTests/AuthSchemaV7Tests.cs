using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // Docs schema v7 names for the identity tables; the rename must keep data and the reviewed runtime grants.
    [PostgresFact]
    public async Task Identity_tables_match_schema_v7_names_constraints_and_runtime_grants()
    {
        async Task<bool> Has(string check) => (bool)(await ScalarAsync("SELECT " + check))!;
        foreach (var table in new[] { "users", "organizations" })
        {
            Assert.True(await Has($"EXISTS(SELECT 1 FROM information_schema.columns WHERE table_name='{table}' AND column_name='profile_revision_no')"), table);
            Assert.False(await Has($"EXISTS(SELECT 1 FROM information_schema.columns WHERE table_name='{table}' AND column_name='profile_revision')"), table);
            Assert.True(await Has($"has_column_privilege('fire3d_api','{table}','profile_revision_no','UPDATE')"), table);
        }
        Assert.False(await Has("has_column_privilege('fire3d_api','users','role','UPDATE')"));
        Assert.False(await Has("EXISTS(SELECT 1 FROM information_schema.columns WHERE table_name='users' AND column_name='avatar_url')"));
        Assert.False(await Has("EXISTS(SELECT 1 FROM information_schema.columns WHERE table_name='organizations' AND column_name='plan')"));
        foreach (var (table, constraint) in new[] { ("users", "check_user_profile_revision"), ("organizations", "check_organization_profile_revision"),
            ("users", "check_user_role_organization"), ("users", "check_user_auth_method"), ("users", "check_trainee_username_required") })
            Assert.True(await Has($"EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='public.{table}'::regclass AND conname='{constraint}' AND convalidated)"), constraint);
        Assert.Equal("text", await ScalarAsync("SELECT data_type FROM information_schema.columns WHERE table_name='user_devices' AND column_name='fcm_token'"));
        Assert.True(await Has("EXISTS(SELECT 1 FROM information_schema.columns WHERE table_name='user_devices' AND column_name='fcm_token_updated_at')"));
        Assert.True(await Has("to_regclass('public.device_active_fcm') IS NOT NULL"));
        Assert.Equal("r", await ScalarAsync("SELECT confdeltype::text FROM pg_constraint WHERE conname='user_devices_user_id_fkey'"));

        // The admin account was created through the API; the database now rejects the v7 invariants directly.
        var organization = Guid.NewGuid();
        await ExecuteAsync($"INSERT INTO organizations(id,name,slug,is_active,metadata,created_at,updated_at) VALUES('{organization}','V7','v7-{organization:N}',true,'{{}}',now(),now())");
        var invalid = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            ExecuteAsync($"UPDATE users SET organization_id='{organization}' WHERE id='{adminId}'"));
        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, invalid.SqlState);
        invalid = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync($"UPDATE users SET profile_revision_no=0 WHERE id='{adminId}'"));
        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, invalid.SqlState);
    }
}
