using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261007110000_AddSupportCommandContracts")]
public sealed class AddSupportCommandContracts:Migration
{
 protected override void Up(MigrationBuilder builder)
 {
  using var stream=typeof(AddSupportCommandContracts).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.SupportCommands.sql")!;builder.Sql(new StreamReader(stream).ReadToEnd());
 }
 protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Support history is forward-only.");
}
