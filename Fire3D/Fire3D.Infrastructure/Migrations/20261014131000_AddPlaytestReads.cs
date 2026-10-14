using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261014131000_AddPlaytestReads")]
public sealed class AddPlaytestReads : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(AddPlaytestReads).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.PlaytestReads.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward migration.");
}
