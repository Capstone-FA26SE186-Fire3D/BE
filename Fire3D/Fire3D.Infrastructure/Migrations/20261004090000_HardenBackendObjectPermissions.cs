using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20261004090000_HardenBackendObjectPermissions")]
public sealed class HardenBackendObjectPermissions : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream = typeof(HardenBackendObjectPermissions).Assembly.GetManifestResourceStream(
            "Fire3D.Infrastructure.Persistence.BackendObjectPermissions.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder) =>
        throw new NotSupportedException("Use a reviewed forward migration; never restore client access to backend data.");
}
