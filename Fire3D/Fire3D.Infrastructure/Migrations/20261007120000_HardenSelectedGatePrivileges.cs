using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261007120000_HardenSelectedGatePrivileges")]
public sealed class HardenSelectedGatePrivileges : Migration
{
 protected override void Up(MigrationBuilder builder)
 {
  using var stream=typeof(HardenSelectedGatePrivileges).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.SelectedGatePrivileges.sql")!;
  builder.Sql(new StreamReader(stream).ReadToEnd());
 }
 protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Gate privilege repair is forward-only.");
}
