using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20260930130000_AddDeviceInstallationHashScheme")]
public sealed class AddDeviceInstallationHashScheme : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE public.device_installations
              ADD COLUMN IF NOT EXISTS secret_hash_scheme varchar(32) NULL;
            ALTER TABLE public.device_installations
              ADD CONSTRAINT device_installations_secret_hash_scheme_check
              CHECK (secret_hash_scheme IS NULL OR secret_hash_scheme IN ('sha256-text-v1', 'sha256-bytes-v2'));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE public.device_installations
              DROP CONSTRAINT IF EXISTS device_installations_secret_hash_scheme_check;
            ALTER TABLE public.device_installations
              DROP COLUMN IF EXISTS secret_hash_scheme;
            """);
    }
}
