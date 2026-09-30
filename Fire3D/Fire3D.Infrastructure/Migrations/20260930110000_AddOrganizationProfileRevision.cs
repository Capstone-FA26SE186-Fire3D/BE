using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20260930110000_AddOrganizationProfileRevision")]
public sealed class AddOrganizationProfileRevision : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("ALTER TABLE public.organizations ADD COLUMN IF NOT EXISTS profile_revision bigint NOT NULL DEFAULT 1 CHECK (profile_revision > 0);");
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("ALTER TABLE public.organizations DROP COLUMN IF EXISTS profile_revision;");
}
