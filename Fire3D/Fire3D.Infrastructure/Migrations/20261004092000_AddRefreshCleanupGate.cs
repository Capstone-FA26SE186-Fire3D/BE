using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20261004092000_AddRefreshCleanupGate")]
public sealed class AddRefreshCleanupGate : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(AddRefreshCleanupGate).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.RefreshCleanupGate.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder) => throw new NotSupportedException("Use a reviewed forward migration for maintenance permissions.");
}
