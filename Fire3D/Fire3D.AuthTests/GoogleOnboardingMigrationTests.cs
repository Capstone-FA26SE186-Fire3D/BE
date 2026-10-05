using Xunit;

namespace Fire3D.AuthTests;

public sealed class GoogleOnboardingMigrationTests
{
    [BillingPostgresFact]
    public async Task Migration_delivers_onboarding_storage_and_preserves_existing_users()
    {
        await using var db = await BillingDatabase.Create(false);
        await GoogleOnboardingPostgresTests.Prepare(db);
        Assert.Equal(true, await db.Scalar("SELECT to_regclass('public.auth_google_onboarding_sessions') IS NOT NULL"));
        Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM users"));
        Assert.Equal(false, await db.Scalar("SELECT has_table_privilege('anon','auth_google_onboarding_sessions','SELECT')"));
        Assert.Equal(false, await db.Scalar("SELECT has_table_privilege('authenticated','auth_google_onboarding_sessions','UPDATE')"));
        Assert.Equal(true, await db.Scalar("SELECT has_table_privilege('fire3d_api','auth_google_onboarding_sessions','INSERT')"));
    }
}
