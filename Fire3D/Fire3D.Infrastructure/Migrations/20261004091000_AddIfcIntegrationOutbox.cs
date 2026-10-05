using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20261004091000_AddIfcIntegrationOutbox")]
public sealed class AddIfcIntegrationOutbox : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(AddIfcIntegrationOutbox).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.IfcIntegrationOutbox.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder) => throw new NotSupportedException("Preserve event history; use a reviewed forward migration.");
}
