using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261010093000_AddReadinessAuditChanges")]
public sealed class AddReadinessAuditChanges : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream = typeof(AddReadinessAuditChanges).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.ReadinessAuditChanges.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder) => throw new NotSupportedException("Roll forward; preserve append-only audit history.");
}
