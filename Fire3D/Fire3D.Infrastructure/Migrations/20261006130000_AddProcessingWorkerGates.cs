using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261006130000_AddProcessingWorkerGates")]
public sealed class AddProcessingWorkerGates:Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(AddProcessingWorkerGates).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.ProcessingWorker.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Processing provenance must not be destructively downgraded.");
}
