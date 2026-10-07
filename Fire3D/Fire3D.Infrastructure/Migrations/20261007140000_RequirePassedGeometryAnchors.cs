using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261007140000_RequirePassedGeometryAnchors")]
public sealed class RequirePassedGeometryAnchors : Migration
{
 protected override void Up(MigrationBuilder builder)
 {
  using var stream=typeof(RequirePassedGeometryAnchors).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.ScenarioAcceptedGeometry.sql")!;
  builder.Sql(new StreamReader(stream).ReadToEnd());
 }
 protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Gate repairs are forward-only.");
}
