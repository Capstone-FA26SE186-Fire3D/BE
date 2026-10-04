using Fire3D.Infrastructure.Ifc;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class IfcOutboxPostgresTests
{
    [BillingPostgresFact]
    public async Task Processing_job_audit_and_canonical_tenant_envelope_commit_together()
    {
        await using var db = await BillingDatabase.Create();
        await BackendDatabasePermissionsTests.Apply(db, "AddIfcIntegrationOutbox");
        var revision = Guid.NewGuid(); var source = Guid.NewGuid();
        await db.Sql($$"""
            INSERT INTO revisions(id,building_id,organization_id,uploaded_by,version_label,primary_type,status,created_at,updated_at)
            VALUES('{{revision}}','{{BillingDatabase.Building}}','{{BillingDatabase.Org}}','{{BillingDatabase.Owner}}','test','IFC','Uploaded',now(),now());
            INSERT INTO source_documents(id,revision_id,uploaded_by,original_filename,file_size_bytes,storage_url,mime_type,sha256_hash,usage_rights,file_type,quarantine_status,created_at)
            VALUES('{{source}}','{{revision}}','{{BillingDatabase.Owner}}','test.ifc',10,'private/source.ifc','application/octet-stream',repeat('a',64),'Private','IFC','Pending',now());
            """);
        await using var context = db.Context();
        var result = await new IfcWriteStore(context).ProcessRevisionAsync(BillingDatabase.Owner, revision, BillingDatabase.Org, default);
        Assert.True(result.IsSuccess);
        Assert.Equal(1L, await db.Scalar($"SELECT count(*) FROM integration_outbox_events WHERE aggregate_id='{result.Value}' AND organization_id='{BillingDatabase.Org}' AND schema_version='1' AND payload_hash=fet3d_jsonb_payload_hash(payload)"));
        Assert.Equal(1L, await db.Scalar($"SELECT count(*) FROM audit_logs WHERE target_id='{result.Value}'"));
        var key = $"ifc-process:{result.Value:N}";
        Assert.Equal("AlreadyEnqueued", await db.Scalar($"SELECT enqueue_integration_outbox_event('{key}','ProcessingJob','{result.Value}','ProcessingJobRequested','1',(SELECT payload FROM integration_outbox_events WHERE idempotency_key='{key}'))"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Sql($"SELECT enqueue_integration_outbox_event('{key}','ProcessingJob','{result.Value}','ProcessingJobRequested','1',jsonb_build_object('job_id','{result.Value}','extra',true))"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Sql($"UPDATE integration_outbox_events SET payload_hash=repeat('b',64) WHERE idempotency_key='{key}'"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Sql($"SELECT enqueue_integration_outbox_event('bad','ProcessingJob','{result.Value}','ProcessingJobRequested','1','{{}}')"));
        Assert.Equal(false, await db.Scalar("SELECT has_table_privilege('fet3d_backend_executor','integration_outbox_events','INSERT')"));
        Assert.Equal(true, await db.Scalar("SELECT has_function_privilege('fet3d_backend_executor','enqueue_integration_outbox_event(text,text,uuid,text,text,jsonb)','EXECUTE')"));
    }

    [BillingPostgresFact]
    public async Task Enqueue_failure_rolls_back_job_and_audit()
    {
        await using var db = await BillingDatabase.Create();
        await BackendDatabasePermissionsTests.Apply(db, "AddIfcIntegrationOutbox");
        var revision=Guid.NewGuid();
        await db.Sql($$"""
            INSERT INTO revisions(id,building_id,organization_id,uploaded_by,version_label,primary_type,status,created_at,updated_at)
            VALUES('{{revision}}','{{BillingDatabase.Building}}','{{BillingDatabase.Org}}','{{BillingDatabase.Owner}}','test','IFC','Uploaded',now(),now());
            INSERT INTO source_documents(id,revision_id,uploaded_by,original_filename,file_size_bytes,storage_url,mime_type,sha256_hash,usage_rights,file_type,quarantine_status,created_at)
            VALUES(gen_random_uuid(),'{{revision}}','{{BillingDatabase.Owner}}','test.ifc',10,'private/source.ifc','application/octet-stream',repeat('a',64),'Private','IFC','Pending',now());
            CREATE FUNCTION fail_outbox_test() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test enqueue failure'; END $$;
            CREATE TRIGGER fail_outbox_test BEFORE INSERT ON integration_outbox_events FOR EACH ROW EXECUTE FUNCTION fail_outbox_test();
            """);
        await using var context=db.Context();
        await Assert.ThrowsAsync<PostgresException>(()=>new IfcWriteStore(context).ProcessRevisionAsync(BillingDatabase.Owner,revision,BillingDatabase.Org,default));
        Assert.Equal(0L,await db.Scalar($"SELECT count(*) FROM processing_jobs WHERE revision_id='{revision}'"));
        Assert.Equal(0L,await db.Scalar("SELECT count(*) FROM audit_logs WHERE target_entity='ProcessingJob'"));
    }
}
