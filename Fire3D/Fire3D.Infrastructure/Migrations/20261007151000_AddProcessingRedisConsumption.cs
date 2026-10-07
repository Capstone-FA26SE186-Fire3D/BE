using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261007151000_AddProcessingRedisConsumption")]
public sealed class AddProcessingRedisConsumption : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream = typeof(AddProcessingRedisConsumption).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.ProcessingRedisConsumption.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder) => throw new NotSupportedException("Handoff receipts require a forward repair.");
}
