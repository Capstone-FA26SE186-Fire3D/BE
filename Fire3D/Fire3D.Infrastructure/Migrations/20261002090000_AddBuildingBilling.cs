using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20261002090000_AddBuildingBilling")]
public sealed class AddBuildingBilling : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream = typeof(AddBuildingBilling).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Billing.Schema.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder) =>
        throw new NotSupportedException("Billing contains financial history. Roll forward with an additive migration.");
}
