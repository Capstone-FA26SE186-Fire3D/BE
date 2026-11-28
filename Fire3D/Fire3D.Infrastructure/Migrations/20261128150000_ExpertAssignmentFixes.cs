using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
/// <summary>BE#245: legacy experts take work, PlatformAdmin no longer decides thẩm định, one lock order, unavailable experts give work back.</summary>
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261128150000_ExpertAssignmentFixes")]
public sealed class ExpertAssignmentFixes : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(ExpertAssignmentFixes).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.ExpertAssignmentFixes.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward migration.");
}
