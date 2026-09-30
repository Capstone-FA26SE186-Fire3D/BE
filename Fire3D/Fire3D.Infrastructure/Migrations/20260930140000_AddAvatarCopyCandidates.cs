using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20260930140000_AddAvatarCopyCandidates")]
public sealed class AddAvatarCopyCandidates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE public.avatar_upload_intents
              ADD COLUMN IF NOT EXISTS expected_profile_revision bigint NULL,
              ADD COLUMN IF NOT EXISTS candidate_attempt_id uuid NULL,
              ADD COLUMN IF NOT EXISTS candidate_object_key text NULL,
              ADD COLUMN IF NOT EXISTS candidate_source_etag text NULL,
              ADD COLUMN IF NOT EXISTS candidate_lease_until timestamptz NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_avatar_upload_intents_candidate_object_key
              ON public.avatar_upload_intents(candidate_object_key) WHERE candidate_object_key IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS public.ux_avatar_upload_intents_candidate_object_key;");
        migrationBuilder.Sql("""
            ALTER TABLE public.avatar_upload_intents
              DROP COLUMN IF EXISTS candidate_lease_until,
              DROP COLUMN IF EXISTS candidate_source_etag,
              DROP COLUMN IF EXISTS candidate_object_key,
              DROP COLUMN IF EXISTS candidate_attempt_id,
              DROP COLUMN IF EXISTS expected_profile_revision;
            """);
    }
}
