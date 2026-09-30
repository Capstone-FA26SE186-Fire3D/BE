using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20260930100000_AddCredentialBoundDeviceInstallations")]
public sealed class AddCredentialBoundDeviceInstallations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS public.device_installations (
                id uuid PRIMARY KEY,
                device_uuid varchar(255) NOT NULL UNIQUE,
                secret_hash varchar(64) NOT NULL CHECK (secret_hash ~ '^[0-9a-f]{64}$'),
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL
            );
            ALTER TABLE public.user_devices ADD COLUMN IF NOT EXISTS installation_id uuid NULL;
            ALTER TABLE public.user_devices ADD COLUMN IF NOT EXISTS notifications_enabled boolean NOT NULL DEFAULT false;
            ALTER TABLE public.user_devices ADD COLUMN IF NOT EXISTS fcm_token_generation integer NOT NULL DEFAULT 0;
            ALTER TABLE public.user_devices ADD COLUMN IF NOT EXISTS revoked_at timestamptz NULL;
            ALTER TABLE public.user_devices ALTER COLUMN fcm_token TYPE varchar(4096);
            ALTER TABLE public.user_devices DROP CONSTRAINT IF EXISTS user_devices_installation_id_fkey;
            ALTER TABLE public.user_devices ADD CONSTRAINT user_devices_installation_id_fkey
                FOREIGN KEY (installation_id) REFERENCES public.device_installations(id) ON DELETE RESTRICT;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_user_devices_active_fcm_token
                ON public.user_devices(fcm_token) WHERE fcm_token IS NOT NULL AND notifications_enabled AND revoked_at IS NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS public.ux_user_devices_active_fcm_token;");
        migrationBuilder.Sql("ALTER TABLE public.user_devices DROP CONSTRAINT IF EXISTS user_devices_installation_id_fkey;");
        migrationBuilder.Sql("ALTER TABLE public.user_devices DROP COLUMN IF EXISTS revoked_at, DROP COLUMN IF EXISTS fcm_token_generation, DROP COLUMN IF EXISTS notifications_enabled, DROP COLUMN IF EXISTS installation_id;");
        migrationBuilder.Sql("DROP TABLE IF EXISTS public.device_installations;");
    }
}
