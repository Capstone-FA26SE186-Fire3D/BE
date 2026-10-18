using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261018120000_MatchPackageDownloadToStart")]
public sealed class MatchPackageDownloadToStart : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(MatchPackageDownloadToStart).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.PackageDownloadMatchesStart.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward migration.");
}
