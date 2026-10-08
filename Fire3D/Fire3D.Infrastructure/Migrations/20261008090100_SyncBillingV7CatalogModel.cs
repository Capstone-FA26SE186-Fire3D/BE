using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
public partial class SyncBillingV7CatalogModel : Migration
{
    // DDL is applied by AddBillingV7Catalog; this migration records the generated EF model.
    protected override void Up(MigrationBuilder builder) { }
    protected override void Down(MigrationBuilder builder) => throw new NotSupportedException("Preserve commercial history; use a reviewed forward migration.");
}