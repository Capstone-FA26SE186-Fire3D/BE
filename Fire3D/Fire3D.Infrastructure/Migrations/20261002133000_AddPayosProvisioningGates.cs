using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))][Migration("20261002133000_AddPayosProvisioningGates")]
public sealed class AddPayosProvisioningGates : Migration
{
 protected override void Up(MigrationBuilder builder)
 {
  // Forward refresh of runtime gates: works for installations that already applied earlier runtime migrations.
  foreach(var resource in new[]{"PayosCheckout.sql","PayosWebhook.sql","PayosProvisioning.sql"})
  {using var stream=typeof(AddPayosProvisioningGates).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Billing."+resource)!;builder.Sql(new StreamReader(stream).ReadToEnd());}
 }
 protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Forward migration required.");
}
