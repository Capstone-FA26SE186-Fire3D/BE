using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20261006120000_AddBoundIfcUploads")]
public sealed class AddBoundIfcUploads : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        using var stream = typeof(AddBoundIfcUploads).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.IfcUploads.sql")!;
        migrationBuilder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("IFC provenance must not be destructively downgraded.");
}
