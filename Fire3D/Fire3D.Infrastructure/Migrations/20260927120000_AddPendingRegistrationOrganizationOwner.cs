using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20260927120000_AddPendingRegistrationOrganizationOwner")]
public sealed class AddPendingRegistrationOrganizationOwner : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE public.organizations
          ADD COLUMN IF NOT EXISTS registration_owner_user_id uuid NULL;
        CREATE INDEX IF NOT EXISTS ix_organizations_registration_owner_user_id
          ON public.organizations(registration_owner_user_id)
          WHERE registration_owner_user_id IS NOT NULL;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP INDEX IF EXISTS public.ix_organizations_registration_owner_user_id;
        ALTER TABLE public.organizations DROP COLUMN IF EXISTS registration_owner_user_id;
        """);
}
