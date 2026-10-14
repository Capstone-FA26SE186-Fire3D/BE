using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261014138000_AddAiContractV2")]
public sealed class AddAiContractV2 : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        foreach (var name in new[] { "AiContractV2.sql", "KnowledgeChunks.sql" })
        {
            using var stream=typeof(AddAiContractV2).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql."+name)!;
            builder.Sql(new StreamReader(stream).ReadToEnd());
        }
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward migration.");
}
