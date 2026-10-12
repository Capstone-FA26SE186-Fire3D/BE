using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261012110000_AddBillingQuotationCancel")]
public sealed class AddBillingQuotationCancel : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(AddBillingQuotationCancel).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Billing.BillingQuotationCancel.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward migration.");
}
