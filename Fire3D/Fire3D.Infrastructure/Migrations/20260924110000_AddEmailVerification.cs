using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20260924110000_AddEmailVerification")]
public partial class AddEmailVerification : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(name: "email_verified_at", table: "users", type: "timestamp with time zone", nullable: true);
        migrationBuilder.Sql("""
            CREATE TABLE public.email_verification_tokens (
              id uuid PRIMARY KEY, user_id uuid NOT NULL REFERENCES public.users(id) ON DELETE CASCADE,
              token_hash char(64) NOT NULL UNIQUE, created_at timestamptz NOT NULL DEFAULT now(),
              expires_at timestamptz NOT NULL, used_at timestamptz NULL);
            CREATE INDEX ix_email_verification_tokens_user_id ON public.email_verification_tokens(user_id);
            CREATE TABLE public.email_verification_jobs (
              id uuid PRIMARY KEY, email text NOT NULL, status text NOT NULL DEFAULT 'Pending', attempts integer NOT NULL DEFAULT 0,
              available_at timestamptz NOT NULL DEFAULT now(), lease_token uuid NULL, lease_until timestamptz NULL,
              created_at timestamptz NOT NULL DEFAULT now());
            CREATE INDEX ix_email_verification_jobs_claim ON public.email_verification_jobs(status, available_at, created_at);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE public.email_verification_jobs; DROP TABLE public.email_verification_tokens;");
        migrationBuilder.DropColumn(name: "email_verified_at", table: "users");
    }
}
