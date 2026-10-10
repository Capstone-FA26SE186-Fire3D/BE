using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261011110000_AddBillingUpgradeTopUp")]
public sealed class AddBillingUpgradeTopUp : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(AddBillingUpgradeTopUp).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Billing.BillingV7UpgradeTopUp.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward migration.");
}
