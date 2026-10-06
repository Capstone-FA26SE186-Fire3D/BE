using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261006140000_AddScenarioAuthoringContracts")]
public sealed class AddScenarioAuthoringContracts:Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(AddScenarioAuthoringContracts).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.ScenarioAuthoring.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Immutable authoring snapshots and receipts must be retained.");
}
