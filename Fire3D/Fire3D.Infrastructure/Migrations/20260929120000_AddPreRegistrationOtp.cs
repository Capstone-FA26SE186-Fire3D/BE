using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20260929120000_AddPreRegistrationOtp")]
public partial class AddPreRegistrationOtp : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS public.registration_email_challenges (
              id uuid PRIMARY KEY,
              email text NOT NULL,
              otp_hash text NOT NULL,
              otp_ciphertext bytea NOT NULL,
              otp_nonce bytea NOT NULL,
              otp_tag bytea NOT NULL,
              expires_at timestamptz NOT NULL,
              failed_attempts integer NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0 AND failed_attempts <= 5),
              verified_at timestamptz NULL,
              superseded_at timestamptz NULL,
              registration_token_hash text NULL,
              registration_token_ciphertext bytea NULL,
              registration_token_nonce bytea NULL,
              registration_token_tag bytea NULL,
              registration_token_expires_at timestamptz NULL,
              registration_token_used_at timestamptz NULL,
              created_at timestamptz NOT NULL DEFAULT now(),
              updated_at timestamptz NOT NULL DEFAULT now(),
              CHECK ((verified_at IS NULL AND registration_token_hash IS NULL AND registration_token_ciphertext IS NULL
                      AND registration_token_nonce IS NULL AND registration_token_tag IS NULL AND registration_token_expires_at IS NULL)
                  OR (verified_at IS NOT NULL AND registration_token_hash IS NOT NULL AND registration_token_ciphertext IS NOT NULL
                      AND registration_token_nonce IS NOT NULL AND registration_token_tag IS NOT NULL AND registration_token_expires_at IS NOT NULL))
            );
            CREATE INDEX IF NOT EXISTS ix_registration_email_challenges_active
              ON public.registration_email_challenges(email, created_at DESC) WHERE superseded_at IS NULL;

            CREATE TABLE IF NOT EXISTS public.registration_otp_email_jobs (
              id uuid PRIMARY KEY,
              challenge_id uuid NOT NULL REFERENCES public.registration_email_challenges(id) ON DELETE CASCADE,
              email text NOT NULL,
              remote_address text NULL,
              status text NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending','Leased','Succeeded','Dead')),
              attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
              available_at timestamptz NOT NULL DEFAULT now(),
              lease_token uuid NULL,
              lease_until timestamptz NULL,
              created_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS ix_registration_otp_email_jobs_claim
              ON public.registration_otp_email_jobs(status, available_at, created_at);
            CREATE INDEX IF NOT EXISTS ix_registration_otp_email_jobs_email_created
              ON public.registration_otp_email_jobs(email, created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_registration_otp_email_jobs_ip_created
              ON public.registration_otp_email_jobs(remote_address, created_at DESC) WHERE remote_address IS NOT NULL;

            ALTER TABLE public.registration_email_challenges ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public.registration_otp_email_jobs ENABLE ROW LEVEL SECURITY;
            REVOKE ALL ON public.registration_email_challenges, public.registration_otp_email_jobs FROM PUBLIC;
            DO $$
            DECLARE target_role text; target_table text; policy_name text;
            BEGIN
              FOREACH target_table IN ARRAY ARRAY['registration_email_challenges','registration_otp_email_jobs'] LOOP
                FOREACH target_role IN ARRAY ARRAY['fet3d_backend_executor','fire3d_api'] LOOP
                IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname=target_role) THEN
                  EXECUTE format('GRANT SELECT,INSERT,UPDATE,DELETE ON public.%I TO %I', target_table, target_role);
                  policy_name := format('registration_otp_%s', replace(target_role, '_', ''));
                  IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename=target_table AND policyname=policy_name) THEN
                    EXECUTE format('CREATE POLICY %I ON public.%I TO %I USING(true) WITH CHECK(true)', policy_name, target_table, target_role);
                  END IF;
                END IF;
                END LOOP;
              END LOOP;
            END $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS public.registration_otp_email_jobs; DROP TABLE IF EXISTS public.registration_email_challenges;");
    }
}
