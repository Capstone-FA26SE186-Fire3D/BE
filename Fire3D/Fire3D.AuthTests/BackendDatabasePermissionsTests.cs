using Fire3D.Infrastructure.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class BackendDatabasePermissionsTests
{
    [BillingPostgresFact]
    public async Task Supabase_default_grants_cannot_expose_backend_rows_or_financial_functions()
    {
        await using var db = await BillingDatabase.Create();
        await db.Sql("""
            DO $$ BEGIN
              IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='anon') THEN CREATE ROLE anon NOLOGIN; END IF;
              IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='authenticated') THEN CREATE ROLE authenticated NOLOGIN; END IF;
              IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN CREATE ROLE fire3d_api NOLOGIN; END IF;
            END $$;
            GRANT USAGE ON SCHEMA public TO anon,authenticated,fire3d_api;
            GRANT SELECT,INSERT,UPDATE,DELETE ON TABLE avatar_upload_intents,avatar_object_cleanups,device_installations,password_reset_tokens,support_ticket_messages TO anon,authenticated,fire3d_api;
            GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public TO anon,authenticated;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO anon,authenticated;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT EXECUTE ON FUNCTIONS TO anon,authenticated;
            """);
        await Apply(db, "HardenBackendObjectPermissions");
        Assert.Equal(0L, await db.Scalar("""
            SELECT count(*) FROM pg_class c WHERE c.relnamespace='public'::regnamespace
              AND c.relname IN ('avatar_upload_intents','avatar_object_cleanups','device_installations','password_reset_tokens','support_ticket_messages')
              AND (NOT c.relrowsecurity OR has_table_privilege('anon',c.oid,'SELECT,INSERT,UPDATE,DELETE')
                OR has_table_privilege('authenticated',c.oid,'SELECT,INSERT,UPDATE,DELETE'))
            """));
        Assert.Equal(0L, await db.Scalar("""
            SELECT count(*) FROM pg_proc p WHERE p.pronamespace='public'::regnamespace AND p.prosecdef
              AND p.proname IN ('bind_payos_checkout','process_payos_inbox','finalize_payos_provisioning','create_pending_payos_payment_request')
              AND (has_function_privilege('anon',p.oid,'EXECUTE') OR has_function_privilege('authenticated',p.oid,'EXECUTE'))
            """));
        Assert.Equal(true, await db.Scalar("SELECT has_function_privilege('fet3d_payos_request_executor','public.bind_payos_checkout(uuid,uuid)','EXECUTE')"));
        Assert.Equal(true, await db.Scalar("SELECT has_function_privilege('fet3d_payos_webhook_executor','public.process_payos_inbox(uuid,uuid)','EXECUTE')"));
        Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM users"));
        Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM avatar_upload_intents"));
        await db.Sql("CREATE TABLE public.future_backend_record(id integer); CREATE FUNCTION public.future_backend_function() RETURNS integer LANGUAGE sql AS 'SELECT 1';");
        Assert.Equal(false, await db.Scalar("SELECT has_table_privilege('anon','public.future_backend_record','SELECT,INSERT,UPDATE,DELETE')"));
        Assert.Equal(false, await db.Scalar("SELECT has_function_privilege('authenticated','public.future_backend_function()','EXECUTE')"));
        await db.Sql("SET ROLE fire3d_api; SELECT count(*) FROM public.avatar_upload_intents; RESET ROLE;");
    }

    internal static async Task Apply(BillingDatabase db, string typeName)
    {
        // Before the fix exists, keep the exposed baseline intact so behavioral assertions reproduce it.
        var type = typeof(AddBuildingBilling).Assembly.GetType("Fire3D.Infrastructure.Migrations." + typeName);
        if (type is null) return;
        var migration = (Migration)Activator.CreateInstance(type)!;
        foreach (var operation in migration.UpOperations.OfType<SqlOperation>()) await db.Sql(operation.Sql);
    }
}
