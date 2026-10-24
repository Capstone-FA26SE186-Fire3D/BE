using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261024110000_DropLegacyV7Functions")]
public sealed class DropLegacyV7Functions : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(DropLegacyV7Functions).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.DropLegacyV7Functions.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward migration.");
}
