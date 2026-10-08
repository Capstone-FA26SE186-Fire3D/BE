using Fire3D.Infrastructure.Billing;
using Xunit;
namespace Fire3D.AuthTests;
public sealed class PayosSchemaTests
{
    [BillingPostgresFact]
    public async Task Runtime_migrations_work_without_superuser_and_restore_temporary_schema_grants()
    {
        await using var db=await BillingDatabase.Create(applyPayos:false);var quote=await PayosCheckoutTests.Accepted(db);
        Assert.Equal(false,await db.Scalar("SELECT has_schema_privilege('fet3d_payos_ledger_owner','public','CREATE')"));
        await db.ApplyPayosAsRestrictedMigration();
        Assert.Equal(3L,await db.Scalar("SELECT count(*) FROM users"));Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM quotations"));
        Assert.Equal(false,await db.Scalar("SELECT has_schema_privilege('fet3d_payos_ledger_owner','public','CREATE')"));
        Assert.Equal("Accepted",await db.Scalar($"SELECT status::text FROM quotations WHERE id='{quote}'"));
        Assert.Equal(true,await db.Scalar("SELECT has_function_privilege('fet3d_payos_webhook_executor','process_payos_inbox(uuid,uuid)','EXECUTE')"));
        Assert.Equal(true,await db.Scalar("SELECT has_function_privilege('fire3d_api','reserve_payos_service_periods(uuid,uuid,uuid)','EXECUTE')"));
        Assert.Equal(false,await db.Scalar("SELECT has_table_privilege('fire3d_api','billing_ai_quota_grants','INSERT')"));
    }
    [BillingPostgresFact]
    public async Task Runtime_migration_adds_durable_input_fencing_and_preserves_users()
    {
        await using var db=await BillingDatabase.Create();
        using var stream=typeof(PayosSdkProvider).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Billing.PayosRuntime.sql");
        if(stream is not null)await db.Sql(new StreamReader(stream).ReadToEnd());
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM information_schema.columns WHERE table_name='billing_checkout_operations' AND column_name='provider_input'"));
        Assert.Equal(1L,await db.Scalar("SELECT count(*) FROM information_schema.columns WHERE table_name='payos_payment_requests' AND column_name='payment_link_id'"));
        Assert.Equal(3L,await db.Scalar("SELECT count(*) FROM users"));
        Assert.Equal(false,await db.Scalar("SELECT has_table_privilege('fet3d_payos_webhook_executor','payment_transactions','INSERT')"));
    }
}
