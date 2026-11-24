using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261124090000_AddFireSafetyExpertRole")]
public sealed class AddFireSafetyExpertRole : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(AddFireSafetyExpertRole).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.FireSafetyExpertRole.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd(),suppressTransaction:true);
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward migration.");
}
