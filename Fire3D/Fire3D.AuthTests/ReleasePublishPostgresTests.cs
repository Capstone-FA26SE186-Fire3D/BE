using Fire3D.Application.Releases;
using Fire3D.Infrastructure.Releases;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;
namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private async Task SeedPublishPaidEntitlement(Guid owner,Guid building)
    {
        var package=Guid.NewGuid();var quote=Guid.NewGuid();var item=Guid.NewGuid();
        var org=(Guid)(await ScalarAsync($"SELECT organization_id FROM buildings WHERE id='{building}'"))!;
        // Explicit legacy fixture via verified-provider ledger gate; no external provider.
        await ExecuteAsync($$"""
            INSERT INTO building_locations(building_id,address,created_at,updated_at) VALUES('{{building}}','Fixture address',now(),now()) ON CONFLICT (building_id) DO UPDATE SET address='Fixture address';
            INSERT INTO service_packages(id,code,name,unit_price,currency,duration_months,features,is_active,created_by,created_at,updated_at) VALUES('{{package}}','paid-{{package:N}}','Paid fixture',100,'VND',1,'{}',true,'{{adminId}}',now(),now());
            INSERT INTO quotations(id,organization_id,requested_by,quotation_number,quantity,unit_price,subtotal_amount,tax_amount,discount_amount,total_amount,currency,valid_until,created_at,updated_at) VALUES('{{quote}}','{{org}}','{{owner}}','paid-{{quote:N}}',1,100,100,0,0,100,'VND',now()+interval '1 day',now(),now());
            INSERT INTO quotation_building_items(id,quotation_id,building_id,service_package_id,purchase_action,service_duration_months,unit_price,discount_amount,subtotal_amount,total_amount,currency,price_snapshot,terms_snapshot,discount_snapshot,line_provisioning_key,created_at,updated_at) VALUES('{{item}}','{{quote}}','{{building}}','{{package}}','New',1,100,0,100,100,'VND','{}','{}','{}','line:{{item}}',now(),now());
            UPDATE quotations SET status='Issued',issued_by='{{adminId}}',issued_at=now() WHERE id='{{quote}}';UPDATE quotations SET status='Accepted' WHERE id='{{quote}}';
            SELECT create_pending_payos_payment_request('{{quote}}','{{owner}}','fixture-publish',123456,'https://pay.payos.vn/fixture','https://fet3d.io.vn/return','https://fet3d.io.vn/cancel',now()+interval '1 hour');
            SELECT apply_verified_payos_webhook((SELECT id FROM payos_payment_requests WHERE quotation_id='{{quote}}'),'fake-publish','fake-publish-ref',123456,100,'VND','{}');
            INSERT INTO service_entitlements(id,organization_id,building_id,service_package_id,quotation_id,quotation_item_id,payment_transaction_id,provisioning_key,status,starts_at,ends_at,created_by)
            SELECT gen_random_uuid(),'{{org}}','{{building}}','{{package}}','{{quote}}','{{item}}',id,'service:{{item}}:'||id,'Active',now()-interval '1 minute',now()+interval '1 day','{{owner}}' FROM payment_transactions WHERE provider_transaction_id='fake-publish-ref' AND status='Applied';
            """);
    }

    [PostgresFact]
    public async Task Publish_gate_requires_paid_service_and_commits_publication_receipt_audit_once()
    {
        var f=await SeedReadyPackage();var input=await ApproveRelease(f);var family=await SeedPlaytestFamily(f.Owner);
        var building=(Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;
        await WithReleaseRuntime(async runtime=>
        {
            await using var db=BuildingContext(runtime);await using var secondDb=BuildingContext(runtime);
            var store=new ReleaseWriteStore(db,TimeProvider.System,Options.Create(new PublishingOptions{Enabled=true}));
            var second=new ReleaseWriteStore(secondDb,TimeProvider.System,Options.Create(new PublishingOptions{Enabled=true}));
            var built=await store.BuildAsync(f.Owner,null,input,default,"build",family);Assert.True(built.IsSuccess,built.Error?.Code);
            Assert.Equal("UNAUTHORIZED",(await store.PublishAsync(f.Owner,built.Value!.Id,null,default,Guid.NewGuid())).Error?.Code);
            Assert.Equal("BUILDING_ENTITLEMENT_REQUIRED",(await store.PublishAsync(f.Owner,built.Value.Id,null,default,family)).Error?.Code);
            await SeedTrial(f.Owner,building,2);
            Assert.Equal("BUILDING_ENTITLEMENT_REQUIRED",(await store.PublishAsync(f.Owner,built.Value.Id,null,default,family)).Error?.Code);
            await SeedPublishPaidEntitlement(f.Owner,building);
            await ExecuteAsync("ALTER TABLE audit_logs ADD CONSTRAINT publish_audit_fault CHECK(action<>'Publish') NOT VALID");
            await Assert.ThrowsAsync<PostgresException>(()=>store.PublishAsync(f.Owner,built.Value.Id,null,default,family));
            Assert.Equal("Built",await ScalarAsync($"SELECT status::text FROM releases WHERE id='{built.Value.Id}'"));
            Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM release_command_receipts WHERE operation='Publish'"));
            await ExecuteAsync("ALTER TABLE audit_logs DROP CONSTRAINT publish_audit_fault");
            var results=await Task.WhenAll(store.PublishAsync(f.Owner,built.Value.Id,null,default,family),second.PublishAsync(f.Owner,built.Value.Id,null,default,family));
            Assert.All(results,x=>Assert.True(x.IsSuccess,x.Error?.Code));
            Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM audit_logs WHERE action='Publish'"));
            Assert.Equal(1L,await ScalarAsync("SELECT count(*) FROM release_command_receipts WHERE operation='Publish'"));
            Assert.True((await store.RevokeAsync(f.Owner,built.Value.Id,null,"Test revoke",default,family)).IsSuccess);
            Assert.Equal("RELEASE_NOT_BUILT",(await store.PublishAsync(f.Owner,built.Value.Id,null,default,family)).Error?.Code);
        });
    }

    [PostgresFact]
    public async Task Publish_rechecks_blockers_provenance_and_lifecycle_without_writing_publication()
    {
        var f=await SeedReadyPackage();var input=await ApproveRelease(f);var family=await SeedPlaytestFamily(f.Owner);
        var building=(Guid)(await ScalarAsync($"SELECT building_id FROM revisions WHERE id='{f.Revision}'"))!;
        await SeedPublishPaidEntitlement(f.Owner,building);
        await WithReleaseRuntime(async runtime=>
        {
            await using var db=BuildingContext(runtime);var store=new ReleaseWriteStore(db,TimeProvider.System,Options.Create(new PublishingOptions{Enabled=true}));
            var built=await store.BuildAsync(f.Owner,null,input,default,"build",family);Assert.True(built.IsSuccess,built.Error?.Code);
            await ExecuteAsync($"UPDATE processing_jobs SET current_attempt_id=NULL WHERE id=(SELECT job_id FROM validation_runs WHERE id='{f.Run}')");
            Assert.Equal("RELEASE_READINESS_REQUIRED",(await store.PublishAsync(f.Owner,built.Value!.Id,null,default,family)).Error?.Code);
            await ExecuteAsync($"UPDATE organizations SET is_active=false WHERE id=(SELECT organization_id FROM buildings WHERE id='{building}')");
            Assert.Equal("NOT_FOUND",(await store.PublishAsync(f.Owner,built.Value.Id,null,default,family)).Error?.Code);
            Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM release_command_receipts WHERE operation='Publish'"));
        });
    }
}
