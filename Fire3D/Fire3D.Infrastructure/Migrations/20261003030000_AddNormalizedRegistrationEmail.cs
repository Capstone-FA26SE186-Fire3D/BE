using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20261003030000_AddNormalizedRegistrationEmail")]
public sealed class AddNormalizedRegistrationEmail : Migration
{
    protected override void Up(MigrationBuilder builder) => builder.Sql("""
        DO $$ BEGIN
          IF EXISTS (SELECT 1 FROM public.users GROUP BY lower(btrim(email)) HAVING count(*) > 1) THEN
            RAISE EXCEPTION 'Normalized email duplicates exist. Review identities before applying users_email_normalized_key; no accounts were merged or deleted.';
          END IF;
        END $$;
        CREATE UNIQUE INDEX users_email_normalized_key ON public.users (lower(btrim(email)));
        """);

    protected override void Down(MigrationBuilder builder) => builder.Sql("DROP INDEX public.users_email_normalized_key;");
}
