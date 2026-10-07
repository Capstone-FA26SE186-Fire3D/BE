using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261006160000_AddPlaytestLifecycle")]
public sealed class AddPlaytestLifecycle:Migration
{
 protected override void Up(MigrationBuilder builder)
 {
  using var stream=typeof(AddPlaytestLifecycle).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.PlaytestLifecycle.sql")!;builder.Sql(new StreamReader(stream).ReadToEnd());
 }
 protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Playtest provenance is forward-only.");
}
