using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261008090000_AddBillingV7Catalog")]
public sealed class AddBillingV7Catalog : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(AddBillingV7Catalog).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Billing.BillingV7Catalog.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward migration; retain commercial history.");
}
