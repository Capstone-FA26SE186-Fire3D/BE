using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261007152000_AddProcessingRedisRecovery")]
public sealed class AddProcessingRedisRecovery : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream = typeof(AddProcessingRedisRecovery).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.ProcessingRedisRecovery.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder) => throw new NotSupportedException("Recovery evidence requires a forward repair.");
}
