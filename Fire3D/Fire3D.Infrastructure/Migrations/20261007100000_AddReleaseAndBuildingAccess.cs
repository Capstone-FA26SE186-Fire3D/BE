using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261007100000_AddReleaseAndBuildingAccess")]
public sealed class AddReleaseAndBuildingAccess:Migration
{
 protected override void Up(MigrationBuilder builder)
 {
  using var stream=typeof(AddReleaseAndBuildingAccess).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.ReleaseAccess.sql")!;builder.Sql(new StreamReader(stream).ReadToEnd());
 }
 protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Release provenance is forward-only.");
}
