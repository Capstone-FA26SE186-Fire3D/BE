using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))][Migration("20261002123000_AddPayosCheckoutGates")]
public sealed class AddPayosCheckoutGates : Migration
{
 protected override void Up(MigrationBuilder builder){using var stream=typeof(AddPayosCheckoutGates).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Billing.PayosCheckout.sql")!;builder.Sql(new StreamReader(stream).ReadToEnd());}
 protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Forward migration required.");
}
