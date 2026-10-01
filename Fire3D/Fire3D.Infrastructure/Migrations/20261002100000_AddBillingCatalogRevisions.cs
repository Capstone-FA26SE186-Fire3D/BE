using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261002100000_AddBillingCatalogRevisions")]
public sealed class AddBillingCatalogRevisions : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        builder.Sql("""
        ALTER TABLE service_packages ADD COLUMN IF NOT EXISTS revision bigint NOT NULL DEFAULT 1;
        ALTER TABLE service_package_discount_rules ADD COLUMN IF NOT EXISTS revision bigint NOT NULL DEFAULT 1;
        ALTER TABLE billing_command_receipts ADD COLUMN IF NOT EXISTS response jsonb;
        """);
        using var stream=typeof(AddBillingCatalogRevisions).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Billing.Hardening.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Preserve billing history with a forward migration.");
}
