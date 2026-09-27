using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20260927140000_TightenEmailVerificationRoleGrants")]
public sealed class TightenEmailVerificationRoleGrants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE public.email_verification_tokens FORCE ROW LEVEL SECURITY;
        ALTER TABLE public.email_verification_jobs FORCE ROW LEVEL SECURITY;
        DO $$
        DECLARE target_role text;
        BEGIN
          FOREACH target_role IN ARRAY ARRAY['anon', 'authenticated'] LOOP
            IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=target_role) THEN
              EXECUTE format('REVOKE ALL PRIVILEGES ON public.email_verification_tokens, public.email_verification_jobs FROM %I', target_role);
            END IF;
          END LOOP;
        END $$;
        """);

    // Permissions are intentionally not restored on rollback. Re-enabling broad client access
    // must be a separate, reviewed deployment decision.
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE public.email_verification_tokens FORCE ROW LEVEL SECURITY;
        ALTER TABLE public.email_verification_jobs FORCE ROW LEVEL SECURITY;
        """);
}
