using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20261007160000_AddPersonalPhoneUniqueness")]
public sealed class AddPersonalPhoneUniqueness : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream = typeof(AddPersonalPhoneUniqueness).Assembly.GetManifestResourceStream(
            "Fire3D.Infrastructure.Persistence.PersonalPhonePreflight.sql")
            ?? throw new InvalidOperationException("Personal phone preflight resource is missing.");
        using var reader = new StreamReader(stream);
        var preflight = reader.ReadToEnd().Trim().TrimEnd(';');
        builder.Sql($$"""
            LOCK TABLE public.users IN SHARE ROW EXCLUSIVE MODE;
            DO $phone_guard$ BEGIN
                IF EXISTS (SELECT 1 FROM (
                    {{preflight}}
                ) AS phone_issue) THEN
                    RAISE EXCEPTION 'Personal phone preflight failed. Review the masked report; no accounts or phone numbers were changed.';
                END IF;
            END $phone_guard$;
            CREATE UNIQUE INDEX users_phone_normalized_key
                ON public.users (regexp_replace(phone_number, '[^0-9+]', '', 'g'))
                WHERE phone_number IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder builder) =>
        throw new NotSupportedException("Remove personal phone uniqueness through a reviewed forward migration.");
}
