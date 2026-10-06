using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20261006110000_AddOrganizationPhoneUniqueness")]
public sealed class AddOrganizationPhoneUniqueness : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream = typeof(AddOrganizationPhoneUniqueness).Assembly.GetManifestResourceStream(
            "Fire3D.Infrastructure.Persistence.OrganizationPhonePreflight.sql")
            ?? throw new InvalidOperationException("Organization phone preflight resource is missing.");
        using var reader = new StreamReader(stream);
        var preflight = reader.ReadToEnd().Trim().TrimEnd(';');
        // EF executes this in its migration transaction. Prevent a write between the guard and index creation.
        builder.Sql($$"""
            LOCK TABLE public.organizations IN SHARE ROW EXCLUSIVE MODE;
            DO $phone_guard$ BEGIN
                IF EXISTS (SELECT 1 FROM (
                    {{preflight}}
                ) AS phone_issue) THEN
                    RAISE EXCEPTION 'Organization phone preflight failed. Review the masked read-only report before retrying; no organizations were merged, deleted or rewritten.';
                END IF;
            END $phone_guard$;
            CREATE UNIQUE INDEX organizations_phone_normalized_key
                ON public.organizations (regexp_replace(phone, '[^0-9+]', '', 'g'))
                WHERE phone IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder builder) =>
        throw new NotSupportedException("Remove organization phone uniqueness only through a reviewed forward migration.");
}
