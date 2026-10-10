using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20261008093000_AddReleasePublishGate")]
public sealed class AddReleasePublishGate : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(AddReleasePublishGate).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.ReleasePublish.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Preserve publication history; use a forward migration.");
}
