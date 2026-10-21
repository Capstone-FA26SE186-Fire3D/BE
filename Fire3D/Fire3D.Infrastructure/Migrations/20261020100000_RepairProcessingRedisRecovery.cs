using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20261020100000_RepairProcessingRedisRecovery")]
public sealed class RepairProcessingRedisRecovery : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream = typeof(RepairProcessingRedisRecovery).Assembly.GetManifestResourceStream(
            "Fire3D.Infrastructure.Persistence.Sql.ProcessingRedisRetryRepair.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }

    protected override void Down(MigrationBuilder builder) =>
        throw new NotSupportedException("Delivery retry receipts require a reviewed forward repair.");
}
