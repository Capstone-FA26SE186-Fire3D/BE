using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // DropDeadFunctions: superseded functions are gone; the provisioning trigger keeps the function it runs today.
    [PostgresFact]
    public async Task Dead_functions_are_dropped_and_the_provisioning_trigger_is_kept()
    {
        foreach (var name in new[] { "ai_request_representation", "validate_payment_provisioning_write", "append_session_event", "assert_org_user",
                     "complete_training_session", "confirm_revision_for_training", "lock_content" })
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM pg_proc WHERE pronamespace='public'::regnamespace AND proname='{name}'"));
        Assert.Equal("validate_payment_provisioning_purpose_write", await ScalarAsync(
            "SELECT p.proname::text FROM pg_trigger t JOIN pg_proc p ON p.oid=t.tgfoid WHERE t.tgname='billing_payment_provisioning_records'"));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM pg_proc WHERE pronamespace='public'::regnamespace AND proname='apply_verified_payos_webhook'"));
    }
}
