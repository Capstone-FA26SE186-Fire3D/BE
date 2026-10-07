using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261007150000_AddProcessingRedisDelivery")]
public sealed class AddProcessingRedisDelivery : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream = typeof(AddProcessingRedisDelivery).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.ProcessingRedisDelivery.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder) => throw new NotSupportedException("Delivery provenance requires a forward repair.");
}
