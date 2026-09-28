using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20260928160000_AddAvatarCleanupRecovery")]
public partial class AddAvatarCleanupRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE public.avatar_upload_intents ADD COLUMN IF NOT EXISTS final_object_key text;");
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS public.avatar_object_cleanups (
                id uuid PRIMARY KEY,
                object_key text NOT NULL UNIQUE,
                available_at timestamptz NOT NULL,
                lease_token uuid NULL,
                lease_until timestamptz NULL,
                attempts integer NOT NULL DEFAULT 0,
                created_at timestamptz NOT NULL,
                CONSTRAINT ck_avatar_object_cleanups_attempts CHECK (attempts >= 0)
            );
            CREATE INDEX IF NOT EXISTS ix_avatar_object_cleanups_available_lease
                ON public.avatar_object_cleanups(available_at, lease_until);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS public.avatar_object_cleanups;");
        migrationBuilder.Sql("ALTER TABLE public.avatar_upload_intents DROP COLUMN IF EXISTS final_object_key;");
    }
}
