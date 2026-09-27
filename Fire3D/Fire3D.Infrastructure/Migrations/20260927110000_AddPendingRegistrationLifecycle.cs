using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20260927110000_AddPendingRegistrationLifecycle")]
public sealed class AddPendingRegistrationLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE public.users
              ADD COLUMN IF NOT EXISTS registration_expires_at timestamptz NULL;

            ALTER TABLE public.email_verification_jobs
              ADD COLUMN IF NOT EXISTS user_id uuid NULL,
              ADD COLUMN IF NOT EXISTS generation integer NOT NULL DEFAULT 1;

            UPDATE public.email_verification_jobs j
               SET user_id = u.id
              FROM public.users u
             WHERE j.user_id IS NULL AND j.email = u.email;

            -- Existing unowned jobs are historical and cannot safely mail or mutate an account.
            UPDATE public.email_verification_jobs SET status='Dead', lease_token=NULL, lease_until=NULL
             WHERE user_id IS NULL AND status IN ('Pending','Leased');

            CREATE INDEX IF NOT EXISTS ix_email_verification_jobs_user_generation
              ON public.email_verification_jobs(user_id, generation, created_at);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS public.ix_email_verification_jobs_user_generation;");
        migrationBuilder.Sql("ALTER TABLE public.email_verification_jobs DROP COLUMN IF EXISTS generation;");
        migrationBuilder.Sql("ALTER TABLE public.email_verification_jobs DROP COLUMN IF EXISTS user_id;");
        migrationBuilder.Sql("ALTER TABLE public.users DROP COLUMN IF EXISTS registration_expires_at;");
    }
}
