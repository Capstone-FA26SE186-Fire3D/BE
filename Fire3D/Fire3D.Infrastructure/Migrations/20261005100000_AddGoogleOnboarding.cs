using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20261005100000_AddGoogleOnboarding")]
public sealed class AddGoogleOnboarding : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream = typeof(AddGoogleOnboarding).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.GoogleOnboarding.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder) => throw new NotSupportedException("Use a reviewed forward migration for Google identity recovery.");
}
