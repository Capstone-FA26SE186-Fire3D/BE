using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261008094000_AddReportingReadPermissions")]
public sealed class AddReportingReadPermissions : Migration
{
    protected override void Up(MigrationBuilder builder)=>builder.Sql("""
        -- Trusted API only. JWT authorization is rechecked by the query service.
        -- Do not grant any ledger/provenance mutation or expose tables to browser roles.
        GRANT SELECT ON users,organizations,buildings,revisions,processing_jobs,support_tickets,audit_logs,auth_refresh_tokens TO fire3d_api;
        DO $read_access$ DECLARE t text; BEGIN
            FOREACH t IN ARRAY ARRAY['users','organizations','buildings','revisions','processing_jobs','support_tickets','audit_logs','auth_refresh_tokens'] LOOP
                EXECUTE format('CREATE POLICY reporting_api_read ON %I FOR SELECT TO fire3d_api USING(true)',t);
            END LOOP;
        END $read_access$;
        """);
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward permission migration to avoid breaking existing readers.");
}
