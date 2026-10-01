using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class BillingSchemaTests
{
    [BillingPostgresFact]
    public async Task Payment_gates_derive_amount_reject_mismatch_and_apply_duplicate_once()
    {
        await using var fixture = await BillingDatabase.Create();
        await fixture.Sql($$"""
            INSERT INTO service_packages(id,code,name,unit_price,currency,duration_months,features,is_active,created_by,created_at,updated_at)
            VALUES ('40000000-0000-0000-0000-000000000009','GATE','Package',100,'VND',1,'{}',true,'{{BillingDatabase.Admin}}',now(),now());
            INSERT INTO quotations(id,organization_id,requested_by,quotation_number,quantity,unit_price,subtotal_amount,tax_amount,discount_amount,total_amount,currency,valid_until,created_at,updated_at)
            VALUES ('50000000-0000-0000-0000-000000000009','{{BillingDatabase.Org}}','{{BillingDatabase.Owner}}','GATE',1,100,100,0,0,100,'VND',now()+interval '1 day',now(),now());
            INSERT INTO quotation_building_items(id,quotation_id,building_id,service_package_id,purchase_action,service_duration_months,
              unit_price,discount_amount,subtotal_amount,total_amount,currency,price_snapshot,terms_snapshot,discount_snapshot,line_provisioning_key,created_at,updated_at)
            VALUES('60000000-0000-0000-0000-000000000009','50000000-0000-0000-0000-000000000009','{{BillingDatabase.Building}}','40000000-0000-0000-0000-000000000009',
              'New',1,100,0,100,100,'VND','{}','{}','{}','line:gate',now(),now());
            UPDATE quotations SET status='Issued',issued_by='{{BillingDatabase.Admin}}',issued_at=now() WHERE quotation_number='GATE';
            UPDATE quotations SET status='Accepted' WHERE quotation_number='GATE';
            SET ROLE fet3d_payos_request_executor;
            SELECT create_pending_payos_payment_request('50000000-0000-0000-0000-000000000009','{{BillingDatabase.Owner}}','gate',123456,'https://pay.payos.vn/gate','https://fet3d.io.vn/return','https://fet3d.io.vn/cancel',now()+interval '1 hour');
            """);
        Assert.Equal(100m,await fixture.Scalar("SELECT expected_amount FROM payos_payment_requests WHERE order_code=123456"));
        var requestId = (Guid)(await fixture.Scalar("SELECT id FROM payos_payment_requests WHERE order_code=123456"))!;
        await fixture.Sql($"SET ROLE fet3d_payos_webhook_executor; SELECT apply_verified_payos_webhook('{requestId}','bad','bad-ref',123456,99,'VND','{{}}')");
        Assert.Equal("Pending",await fixture.Scalar("SELECT status::text FROM payos_payment_requests WHERE order_code=123456"));
        var valid = $"SET ROLE fet3d_payos_webhook_executor; SELECT apply_verified_payos_webhook('{requestId}','good','good-ref',123456,100,'VND','{{}}')";
        await fixture.Sql(valid); await fixture.Sql(valid);
        Assert.Equal("Paid",await fixture.Scalar("SELECT status::text FROM payos_payment_requests WHERE order_code=123456"));
        Assert.Equal(1L,await fixture.Scalar("SELECT count(*) FROM payment_transactions WHERE status='Applied'"));
        Assert.Equal(0L,await fixture.Scalar("SELECT count(*) FROM service_entitlements"));
    }

    [BillingPostgresFact]
    public async Task Billing_migration_preserves_legacy_data_and_installs_execute_only_payment_roles()
    {
        await using var fixture = await BillingDatabase.Create(false);
        await fixture.Sql($$"""
            INSERT INTO service_packages(id,code,name,unit_price,currency,duration_months,features,is_active,created_by,created_at,updated_at)
            VALUES ('40000000-0000-0000-0000-000000000001','OLD','Old',100,'VND',1,'{}',true,'{{BillingDatabase.Admin}}',now(),now());
            INSERT INTO quotations(id,organization_id,service_package_id,requested_by,quotation_number,status,quantity,unit_price,
                subtotal_amount,tax_amount,discount_amount,total_amount,currency,valid_until,created_at,updated_at)
            VALUES ('50000000-0000-0000-0000-000000000001','{{BillingDatabase.Org}}','40000000-0000-0000-0000-000000000001',
                '{{BillingDatabase.Owner}}','OLD-1','Draft',1,100,100,0,0,100,'VND',now()+interval '1 day',now(),now());
            """);
        await fixture.ApplyBilling();
        Assert.Equal(1L, await fixture.Scalar("SELECT count(*) FROM quotations WHERE quotation_number='OLD-1'"));
        Assert.Equal(false, await fixture.Scalar("SELECT has_table_privilege('fet3d_payos_request_executor','payos_payment_requests','INSERT')"));
        Assert.Equal(false, await fixture.Scalar("SELECT has_table_privilege('fet3d_payos_webhook_executor','payment_transactions','UPDATE')"));
        Assert.Equal(true, await fixture.Scalar("SELECT has_function_privilege('fet3d_payos_request_executor','create_pending_payos_payment_request(uuid,uuid,text,bigint,text,text,text,timestamptz)','EXECUTE')"));
        Assert.Equal(false, await fixture.Scalar("SELECT rolcanlogin FROM pg_roles WHERE rolname='fet3d_payos_ledger_owner'"));
        Assert.Equal(1L,await fixture.Scalar("SELECT count(*) FROM pg_constraint WHERE conname='fk_billing_quotation_discount' AND conrelid='quotations'::regclass"));
        var denied = await Assert.ThrowsAsync<PostgresException>(() => fixture.Sql("SET ROLE fet3d_payos_request_executor; INSERT INTO payos_payment_requests(id) VALUES (gen_random_uuid())"));
        Assert.Equal("42501",denied.SqlState);
    }

    [BillingPostgresFact]
    public async Task Quotation_lines_enforce_building_tenant_and_positive_duration()
    {
        await using var fixture = await BillingDatabase.Create();
        await fixture.Sql($$"""
            INSERT INTO service_packages(id,code,name,unit_price,currency,duration_months,features,is_active,created_by,created_at,updated_at)
            VALUES ('40000000-0000-0000-0000-000000000002','P','Package',100,'VND',1,'{}',true,'{{BillingDatabase.Admin}}',now(),now());
            INSERT INTO quotations(id,organization_id,requested_by,quotation_number,status,quantity,unit_price,subtotal_amount,tax_amount,discount_amount,total_amount,currency,valid_until,created_at,updated_at)
            VALUES ('50000000-0000-0000-0000-000000000002','{{BillingDatabase.OtherOrg}}','{{BillingDatabase.Other}}','Q-1','Draft',1,100,100,0,0,100,'VND',now()+interval '1 day',now(),now());
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => fixture.Sql($$"""
            INSERT INTO quotation_building_items(id,quotation_id,building_id,service_package_id,purchase_action,service_duration_months,
              unit_price,discount_amount,subtotal_amount,total_amount,currency,price_snapshot,terms_snapshot,discount_snapshot,created_at,updated_at)
            VALUES(gen_random_uuid(),'50000000-0000-0000-0000-000000000002','{{BillingDatabase.Building}}','40000000-0000-0000-0000-000000000002',
              'New',1,100,0,100,100,'VND','{}','{}','{}',now(),now());
            """));
        Assert.Contains("organization", error.MessageText);
        var moved=await Assert.ThrowsAsync<PostgresException>(()=>fixture.Sql($"UPDATE quotations SET organization_id='{BillingDatabase.Org}' WHERE quotation_number='Q-1'"));
        Assert.Contains("identity",moved.MessageText);
    }

    [BillingPostgresFact]
    public async Task Migration_revokes_legacy_public_access_and_payos_rejects_non_vnd_or_fractional_quotes()
    {
        await using var fixture=await BillingDatabase.Create(false);
        await fixture.Sql("GRANT SELECT ON quotations TO PUBLIC");await fixture.ApplyBilling();
        Assert.Equal(false,await fixture.Scalar("SELECT EXISTS(SELECT 1 FROM information_schema.table_privileges WHERE table_name='quotations' AND grantee='PUBLIC')"));
        foreach(var (currency,amount) in new[]{("USD","100"),("VND","100.50")})
        {
            var quote=Guid.NewGuid();
            await fixture.Sql($$"""
                INSERT INTO quotations(id,organization_id,requested_by,quotation_number,billing_purpose,quantity,unit_price,subtotal_amount,tax_amount,discount_amount,total_amount,currency,valid_until,created_at,updated_at)
                VALUES('{{quote}}','{{BillingDatabase.Org}}','{{BillingDatabase.Owner}}','{{quote}}','AIUsage',1,{{amount}},{{amount}},0,0,{{amount}},'{{currency}}',now()+interval '1 day',now(),now());
                UPDATE quotations SET status='Issued',issued_by='{{BillingDatabase.Admin}}',issued_at=now() WHERE id='{{quote}}';
                UPDATE quotations SET status='Accepted' WHERE id='{{quote}}';
                """);
            await Assert.ThrowsAsync<PostgresException>(()=>fixture.Sql($$"""
                SET ROLE fet3d_payos_request_executor;
                SELECT create_pending_payos_payment_request('{{quote}}','{{BillingDatabase.Owner}}','invalid',765432,'https://pay.payos.vn/invalid','https://fet3d.io.vn/return','https://fet3d.io.vn/cancel',now()+interval '1 hour');
                """));
        }
        Assert.Equal(0L,await fixture.Scalar("SELECT count(*) FROM payos_payment_requests"));
    }
}
