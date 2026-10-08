using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;
namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Billing_v7_preflight_rejects_overlapping_legacy_paid_periods_without_rewriting_history()
    {
        var fixture=await SeedReadyPackage();
        var building=(Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{fixture.Revision}'"))!;
        await SeedPublishPaidEntitlement(fixture.Owner,building,123456);
        await SeedPublishPaidEntitlement(fixture.Owner,building,123457);
        var before=await ScalarAsync("SELECT jsonb_agg(to_jsonb(e) ORDER BY id)::text FROM service_entitlements e");
        Assert.Equal(2L,await ScalarAsync("SELECT count(*) FROM service_entitlements"));
        var sql=new Fire3D.Infrastructure.Migrations.AddBillingV7Provisioning().UpOperations.OfType<SqlOperation>().Single().Sql;
        // Execute the actual forward migration: the preflight must stop it before
        // any schema/history change, even when the old periods are otherwise valid.
        var error=await Assert.ThrowsAsync<PostgresException>(()=>ExecuteAsync(sql));
        Assert.Contains("overlapping paid service history",error.MessageText);
        Assert.Equal(before,await ScalarAsync("SELECT jsonb_agg(to_jsonb(e) ORDER BY id)::text FROM service_entitlements e"));
    }

    [PostgresFact]
    public async Task Provisioning_replay_returns_existing_expired_legacy_entitlement_before_lifecycle_checks()
    {
        var fixture=await SeedReadyPackage();
        var building=(Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{fixture.Revision}'"))!;
        await SeedPublishPaidEntitlement(fixture.Owner,building,123456,expired:true);
        var lease=Guid.NewGuid();var line=Guid.NewGuid();
        var entitlement=(Guid)(await ScalarAsync("SELECT id FROM service_entitlements WHERE payment_transaction_id IS NOT NULL"))!;
        await ExecuteAsync($$"""
            INSERT INTO payment_provisioning_records(id,payment_transaction_id,quotation_id,quotation_item_id,organization_id,provisioning_key,status,lease_token,lease_until)
            SELECT '{{line}}',payment_transaction_id,quotation_id,quotation_item_id,organization_id,provisioning_key,'Pending','{{lease}}',now()+interval '1 minute'
            FROM service_entitlements WHERE id='{{entitlement}}';
            UPDATE buildings SET is_active=false WHERE id='{{building}}';
            """);
        var audits=await ScalarAsync("SELECT count(*) FROM audit_logs");
        var returned=await ScalarAsync($$"""
            SET ROLE fet3d_payos_webhook_executor;
            SELECT finalize_payos_provisioning('{{line}}','{{lease}}',NULL,NULL)
            """);
        Assert.Equal(entitlement,returned);
        Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM service_entitlements WHERE payment_transaction_id IS NOT NULL"));
        Assert.Equal("Succeeded",await ScalarAsync($"SELECT status FROM payment_provisioning_records WHERE id='{line}'"));
        Assert.Equal(audits,await ScalarAsync("SELECT count(*) FROM audit_logs"));
        Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM billing_ai_quota_grants"));
    }
}
