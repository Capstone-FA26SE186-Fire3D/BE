using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261010092000_AddReadinessLiveFamily")]
public sealed class AddReadinessLiveFamily : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream = typeof(AddReadinessLiveFamily).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.ReadinessLiveFamily.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder) => throw new NotSupportedException("Do not restore the unsafe family-less entrypoint.");
}
