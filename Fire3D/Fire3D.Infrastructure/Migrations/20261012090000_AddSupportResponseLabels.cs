using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261012090000_AddSupportResponseLabels")]
public sealed class AddSupportResponseLabels : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(AddSupportResponseLabels).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.Sql.SupportResponseLabels.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward migration.");
}
