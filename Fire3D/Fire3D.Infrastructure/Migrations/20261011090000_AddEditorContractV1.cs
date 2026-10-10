using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261011090000_AddEditorContractV1")]
public sealed class AddEditorContractV1 : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(AddEditorContractV1).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.EditorContractV1.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward migration.");
}
