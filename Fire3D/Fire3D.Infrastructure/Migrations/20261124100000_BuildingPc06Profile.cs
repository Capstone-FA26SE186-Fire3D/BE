using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261124100000_BuildingPc06Profile")]
public sealed class BuildingPc06Profile : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(BuildingPc06Profile).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.BuildingPc06Profile.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward migration.");
}
