using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20260927130000_HardenEmailVerificationStorage")]
public sealed class HardenEmailVerificationStorage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE public.email_verification_tokens ENABLE ROW LEVEL SECURITY;
        ALTER TABLE public.email_verification_jobs ENABLE ROW LEVEL SECURITY;
        REVOKE ALL ON public.email_verification_tokens, public.email_verification_jobs FROM PUBLIC;
        DO $$
        DECLARE target_table text;
        BEGIN
          FOREACH target_table IN ARRAY ARRAY['email_verification_tokens','email_verification_jobs'] LOOP
            IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_backend_executor') THEN
              EXECUTE format('GRANT SELECT,INSERT,UPDATE,DELETE ON public.%I TO fet3d_backend_executor', target_table);
              IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename=target_table AND policyname='email_verification_backend') THEN
                EXECUTE format('CREATE POLICY email_verification_backend ON public.%I TO fet3d_backend_executor USING(true) WITH CHECK(true)', target_table);
              END IF;
            END IF;
            IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN
              EXECUTE format('GRANT SELECT,INSERT,UPDATE,DELETE ON public.%I TO fire3d_api', target_table);
              IF NOT EXISTS(SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename=target_table AND policyname='email_verification_api') THEN
                EXECUTE format('CREATE POLICY email_verification_api ON public.%I TO fire3d_api USING(true) WITH CHECK(true)', target_table);
              END IF;
            END IF;
          END LOOP;
        END $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP POLICY IF EXISTS email_verification_backend ON public.email_verification_tokens;
        DROP POLICY IF EXISTS email_verification_api ON public.email_verification_tokens;
        DROP POLICY IF EXISTS email_verification_backend ON public.email_verification_jobs;
        DROP POLICY IF EXISTS email_verification_api ON public.email_verification_jobs;
        DO $$
        BEGIN
          IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fet3d_backend_executor') THEN
            REVOKE ALL ON public.email_verification_tokens, public.email_verification_jobs FROM fet3d_backend_executor;
          END IF;
          IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='fire3d_api') THEN
            REVOKE ALL ON public.email_verification_tokens, public.email_verification_jobs FROM fire3d_api;
          END IF;
        END $$;
        ALTER TABLE public.email_verification_tokens DISABLE ROW LEVEL SECURITY;
        ALTER TABLE public.email_verification_jobs DISABLE ROW LEVEL SECURITY;
        """);
}
