using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261006145000_GrantIfcGateDependencies")]
public sealed class GrantIfcGateDependencies : Migration
{
 protected override void Up(MigrationBuilder migrationBuilder)
 {
  using var stream=typeof(GrantIfcGateDependencies).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.IfcDependencyGrants.sql")!;
  migrationBuilder.Sql(new StreamReader(stream).ReadToEnd());
 }
 protected override void Down(MigrationBuilder migrationBuilder)=>throw new NotSupportedException("Forward-only gate grants.");
}
