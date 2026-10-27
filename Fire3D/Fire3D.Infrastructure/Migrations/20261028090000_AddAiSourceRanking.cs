using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261028090000_AddAiSourceRanking")]
public sealed class AddAiSourceRanking : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(AddAiSourceRanking).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.AiSourceRanking.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward migration.");
}
