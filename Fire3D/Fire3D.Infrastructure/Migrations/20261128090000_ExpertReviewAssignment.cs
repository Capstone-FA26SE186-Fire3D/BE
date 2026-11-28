using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
/// <summary>BE#242: thẩm định items are assigned round-robin to one available FireSafetyExpert; PlatformAdmin reassigns.</summary>
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261128090000_ExpertReviewAssignment")]
public sealed class ExpertReviewAssignment : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(ExpertReviewAssignment).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.ExpertReviewAssignment.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward migration.");
}
