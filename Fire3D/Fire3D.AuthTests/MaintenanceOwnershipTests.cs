using Fire3D.Infrastructure.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class MaintenanceOwnershipTests
{
    [BillingPostgresFact]
    public async Task Non_superuser_role_creator_can_transfer_ownership_and_restore_set_option()
        => await CheckOwnership(false);
    [BillingPostgresFact]
    public async Task Existing_admin_only_membership_keeps_original_set_and_inherit_options()
        => await CheckOwnership(true);
    private static async Task CheckOwnership(bool existing)
    {
        var db=await BillingDatabase.Create();
        var login="migration_"+Guid.NewGuid().ToString("N");
        var integration="integration_"+Guid.NewGuid().ToString("N");
        var maintenance="maintenance_"+Guid.NewGuid().ToString("N");
        try
        {
            await db.Sql($$"""
                CREATE ROLE {{login}} LOGIN CREATEROLE NOSUPERUSER NOBYPASSRLS NOCREATEDB PASSWORD '{{(new NpgsqlConnectionStringBuilder(db.Connection).Password??"").Replace("'","''")}}';
                GRANT USAGE,CREATE ON SCHEMA public TO {{login}};
                ALTER SCHEMA public OWNER TO {{login}};
                DO $$ DECLARE obj record; BEGIN
                  FOR obj IN SELECT relname FROM pg_class WHERE relnamespace='public'::regnamespace AND relkind='r'
                    AND relname IN('processing_jobs','revisions','buildings','organizations','auth_refresh_tokens') LOOP
                    EXECUTE format('ALTER TABLE public.%I OWNER TO {{login}}',obj.relname);
                  END LOOP;
                END $$;
                """);
            var connectionString=new NpgsqlConnectionStringBuilder(db.Connection){Username=login}.ConnectionString;
            await using(var connection=new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();await using var transaction=await connection.BeginTransactionAsync();
                if(existing)await new NpgsqlCommand($"CREATE ROLE {integration} NOLOGIN; CREATE ROLE {maintenance} NOLOGIN",connection,transaction).ExecuteNonQueryAsync();
                foreach(var operation in new AddIfcIntegrationOutbox().UpOperations.Concat(new AddRefreshCleanupGate().UpOperations).OfType<SqlOperation>())
                {
                    var sql=operation.Sql.Replace("fet3d_integration_owner",integration).Replace("fet3d_auth_maintenance_owner",maintenance);
                    await new NpgsqlCommand(sql,connection,transaction).ExecuteNonQueryAsync();
                }
                await transaction.CommitAsync();
            }
            foreach(var owner in new[]{integration,maintenance})
            {
                Assert.Equal(true,await db.Scalar($"SELECT admin_option AND NOT set_option AND NOT inherit_option FROM pg_auth_members WHERE roleid='{owner}'::regrole AND member='{login}'::regrole"));
                Assert.Equal(false,await db.Scalar($"SELECT has_schema_privilege('{owner}','public','CREATE')"));
            }
            Assert.Equal(3L,await db.Scalar("SELECT count(*) FROM users"));
            var revision=Guid.NewGuid();var source=Guid.NewGuid();var job=Guid.NewGuid();var expiredFamily=Guid.NewGuid();
            await db.Sql($$"""
                INSERT INTO revisions(id,building_id,organization_id,uploaded_by,version_label,primary_type,status,created_at,updated_at)
                VALUES('{{revision}}','{{BillingDatabase.Building}}','{{BillingDatabase.Org}}','{{BillingDatabase.Owner}}','test','IFC','Uploaded',now(),now());
                INSERT INTO source_documents(id,revision_id,uploaded_by,original_filename,file_size_bytes,storage_url,mime_type,sha256_hash,usage_rights,file_type,quarantine_status,created_at)
                VALUES('{{source}}','{{revision}}','{{BillingDatabase.Owner}}','test.ifc',10,'private/source.ifc','application/octet-stream',repeat('a',64),'Private','IFC','Pending',now());
                INSERT INTO processing_jobs(id,revision_id,source_document_id,kind,job_key,status,input_hash,created_at)
                VALUES('{{job}}','{{revision}}','{{source}}','ProcessIfc',gen_random_uuid(),'Queued',repeat('a',64),now());
                ALTER TABLE processing_jobs ENABLE ROW LEVEL SECURITY;
                ALTER TABLE revisions ENABLE ROW LEVEL SECURITY;
                ALTER TABLE buildings ENABLE ROW LEVEL SECURITY;
                ALTER TABLE auth_refresh_tokens ENABLE ROW LEVEL SECURITY;
                INSERT INTO auth_refresh_tokens(id,user_id,family_id,token_hash,created_at,expires_at)
                VALUES(gen_random_uuid(),'{{BillingDatabase.Owner}}','{{expiredFamily}}','expired-role-test',now()-interval '20 days',now()-interval '8 days');
                SET ROLE fet3d_backend_executor;
                SELECT public.enqueue_integration_outbox_event('restricted-role-test','ProcessingJob','{{job}}','ProcessingJobRequested','1',jsonb_build_object('job_id','{{job}}'));
                SELECT public.cleanup_expired_refresh_family('{{BillingDatabase.Owner}}','{{BillingDatabase.Owner}}',7);
                SELECT public.cleanup_expired_refresh_family('{{BillingDatabase.Owner}}','{{expiredFamily}}',7);
                RESET ROLE;
                """);
            Assert.Equal(1L,await db.Scalar($"SELECT count(*) FROM integration_outbox_events WHERE organization_id='{BillingDatabase.Org}'"));
            Assert.Equal(0L,await db.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE family_id='{expiredFamily}'"));
            Assert.Equal(1L,await db.Scalar($"SELECT count(*) FROM auth_refresh_tokens WHERE user_id='{BillingDatabase.Owner}' AND family_id='{BillingDatabase.Owner}'"));
        }
        finally
        {
            await db.DisposeAsync();
            await using var admin=new NpgsqlConnection(Environment.GetEnvironmentVariable("FET3D_BILLING_TEST_ADMIN"));await admin.OpenAsync();
            await new NpgsqlCommand($"DROP ROLE IF EXISTS {integration},{maintenance},{login}",admin).ExecuteNonQueryAsync();
        }
    }
}
