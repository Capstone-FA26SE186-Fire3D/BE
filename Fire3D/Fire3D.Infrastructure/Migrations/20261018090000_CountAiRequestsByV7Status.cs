using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261018090000_CountAiRequestsByV7Status")]
public sealed class CountAiRequestsByV7Status : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(CountAiRequestsByV7Status).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.AiUsageAnalyticsV7.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward migration.");
}
