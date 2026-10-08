using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;


public sealed partial class AddBillingV7Provisioning : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(AddBillingV7Provisioning).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Billing.BillingV7Provisioning.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Preserve payment and quota provenance; use a reviewed forward migration.");
}
