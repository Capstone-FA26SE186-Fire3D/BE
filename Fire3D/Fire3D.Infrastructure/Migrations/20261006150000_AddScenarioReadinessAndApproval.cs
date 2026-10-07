using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261006150000_AddScenarioReadinessAndApproval")]
public sealed class AddScenarioReadinessAndApproval:Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(AddScenarioReadinessAndApproval).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.ScenarioReadiness.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Readiness and approval history must be retained.");
}
