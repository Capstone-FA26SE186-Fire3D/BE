using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261002120000_AddPayosRuntime")]
public sealed class AddPayosRuntime : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(AddPayosRuntime).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Billing.PayosRuntime.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Payment data requires a forward migration.");
}
