using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;
namespace Fire3D.AuthTests;
public sealed class SelectedGatePrivilegeMigrationTests
{
 [BillingPostgresFact]
 public async Task Actual_history_upgrade_preserves_legacy_columns_but_removes_custom_runtime_gate_bypasses()
 {
  var source=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FET3D_BILLING_TEST_ADMIN"));
  Assert.Contains(source.Host,new[]{"127.0.0.1","localhost","::1"});Assert.Equal("postgres",source.Database);
  var name="fet3d_acl_test_"+Guid.NewGuid().ToString("N");var login="fet3d_acl_login_"+Guid.NewGuid().ToString("N");var migrationLogin="fet3d_acl_migration_"+Guid.NewGuid().ToString("N");
  await using var admin=new NpgsqlConnection(source.ConnectionString);await admin.OpenAsync();
  await new NpgsqlCommand($"CREATE DATABASE {name}",admin).ExecuteNonQueryAsync();
  source.Database=name;
  try
  {
   await using var db=new Fire3DDbContext(new DbContextOptionsBuilder<Fire3DDbContext>().UseNpgsql(source.ConnectionString).Options);
   var migrator=db.GetService<IMigrator>();await migrator.MigrateAsync("20261006160000_AddPlaytestLifecycle");
   await using var connection=new NpgsqlConnection(source.ConnectionString);await connection.OpenAsync();
   async Task Sql(string sql)=>await new NpgsqlCommand(sql,connection).ExecuteNonQueryAsync();
   async Task<bool> Bool(string sql)=>(bool)(await new NpgsqlCommand(sql,connection).ExecuteScalarAsync())!;
   await Sql($"CREATE ROLE {login} LOGIN NOSUPERUSER NOBYPASSRLS;GRANT fire3d_api TO {login};GRANT USAGE ON SCHEMA public TO {login};GRANT SELECT,INSERT,UPDATE ON buildings TO {login};GRANT INSERT,UPDATE,DELETE ON feedback,support_tickets,support_ticket_messages TO {login};");
   await migrator.MigrateAsync("20261007110000_AddSupportCommandContracts");
   await Sql($"GRANT UPDATE(participation_code_hash) ON buildings TO {login};GRANT EXECUTE ON FUNCTION release_access_gate(text,uuid,uuid,uuid,jsonb,text,bigint),support_command_gate(text,uuid,uuid,uuid,jsonb,text,bigint) TO anon,authenticated");
   Assert.True(await Bool($"SELECT has_column_privilege('{login}','buildings','visibility','UPDATE')"));
   Assert.True(await Bool($"SELECT has_table_privilege('{login}','support_tickets','INSERT')"));
   // Supabase's migration identity is not a superuser. Owner membership used
   // for gate grants must be restored, not inherited by runtime logins.
   await Sql($"CREATE ROLE {migrationLogin} LOGIN NOSUPERUSER NOBYPASSRLS CREATEROLE;GRANT USAGE,CREATE ON SCHEMA public TO {migrationLogin} WITH GRANT OPTION;GRANT fet3d_ifc_upload_owner TO {migrationLogin} WITH ADMIN TRUE,SET FALSE,INHERIT FALSE;");
   // Redis adds FK references, an RLS policy and trusted handoff backfill on these
   // existing tables. Its migration identity must own the affected schema objects;
   // CREATEROLE alone cannot bypass table ownership or REFERENCES permissions.
   // Personal phone uniqueness also requires ownership of users to create its index.
   foreach(var table in new[]{"users","buildings","releases","release_packages","trainings","building_participation_grants","release_build_provenance","release_command_receipts","feedback","support_tickets","support_ticket_messages","support_command_receipts","integration_outbox_events","integration_event_consumptions","processing_delivery_receipts","__EFMigrationsHistory"}) await Sql($"ALTER TABLE \"{table}\" OWNER TO {migrationLogin}");
   await using(var restricted=new Fire3DDbContext(new DbContextOptionsBuilder<Fire3DDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(source.ConnectionString){Username=migrationLogin}.ConnectionString).Options)) await restricted.Database.MigrateAsync();
   Assert.False(await Bool($"SELECT pg_has_role('{migrationLogin}','fet3d_ifc_upload_owner','USAGE')"));
   Assert.False(await Bool($"SELECT pg_has_role('{migrationLogin}','fet3d_ifc_upload_owner','SET')"));
   foreach(var operation in new[]{"INSERT","UPDATE"})
   {
    Assert.True(await Bool($"SELECT has_column_privilege('{login}','buildings','name','{operation}')"));
    foreach(var column in new[]{"visibility","access_revision","participation_code_hash"}) Assert.False(await Bool($"SELECT has_column_privilege('{login}','buildings','{column}','{operation}')"));
   }
   Assert.False(await Bool($"SELECT has_table_privilege('{login}','support_tickets','INSERT')"));
   Assert.True(await Bool($"SELECT has_function_privilege('{login}','release_access_gate(text,uuid,uuid,uuid,jsonb,text,bigint)','EXECUTE')"));
   Assert.False(await Bool("SELECT has_function_privilege('anon','release_access_gate(text,uuid,uuid,uuid,jsonb,text,bigint)','EXECUTE')"));
   await Sql($"SET ROLE {login}");
   await Sql("UPDATE buildings SET name='legacy' WHERE false");
   var denied=await Assert.ThrowsAsync<PostgresException>(()=>Sql("UPDATE buildings SET visibility='Public' WHERE false"));Assert.Equal("42501",denied.SqlState);
   await Sql("RESET ROLE");
   // A future table-wide grant must still not bypass the gate or its audit.
   var org=Guid.NewGuid();var building=Guid.NewGuid();var actor=Guid.NewGuid();
   await Sql($"INSERT INTO organizations(id,name,slug,plan,is_active,metadata,created_at,updated_at) VALUES('{org}','Fixture','fixture-{org:N}','Standard',true,'{{}}',now(),now());INSERT INTO users(id,email,role,is_active,created_at,updated_at) VALUES('{actor}','acl-{actor:N}@example.test','PlatformAdmin',true,now(),now());INSERT INTO buildings(id,organization_id,name,is_active,created_by,created_at,updated_at) VALUES('{building}','{org}','Fixture',true,'{actor}',now(),now());CREATE POLICY {login} ON buildings TO {login} USING(true) WITH CHECK(true);GRANT INSERT,UPDATE ON buildings TO {login};SET ROLE {login}");
   denied=await Assert.ThrowsAsync<PostgresException>(()=>Sql($"UPDATE buildings SET visibility='Public' WHERE id='{building}'"));Assert.Equal("42501",denied.SqlState);
   denied=await Assert.ThrowsAsync<PostgresException>(()=>Sql($"INSERT INTO buildings(id,organization_id,name,is_active,visibility,created_by,created_at,updated_at) VALUES(gen_random_uuid(),'{org}','Bypass',true,'Public','{actor}',now(),now())"));Assert.Equal("42501",denied.SqlState);
   await Sql($"UPDATE buildings SET name='Legacy write works' WHERE id='{building}';RESET ROLE");
   Assert.True(await Bool($"SELECT visibility='Private' AND access_revision=1 AND name='Legacy write works' FROM buildings WHERE id='{building}'"));
  }
  finally
  {
   NpgsqlConnection.ClearAllPools();await new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)",admin).ExecuteNonQueryAsync();
   await new NpgsqlCommand($"DROP ROLE IF EXISTS {login};DROP ROLE IF EXISTS {migrationLogin}",admin).ExecuteNonQueryAsync();
  }
 }
}
