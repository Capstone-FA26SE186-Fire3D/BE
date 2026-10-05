using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20261006090000_AddGoogleOnboardingDisplayName")]
public sealed class AddGoogleOnboardingDisplayName : Migration
{
    protected override void Up(MigrationBuilder builder) =>
        builder.Sql("ALTER TABLE public.auth_google_onboarding_sessions ADD COLUMN display_name varchar(200);");
    protected override void Down(MigrationBuilder builder) =>
        throw new NotSupportedException("Preserve identity metadata with a reviewed forward migration.");
}
