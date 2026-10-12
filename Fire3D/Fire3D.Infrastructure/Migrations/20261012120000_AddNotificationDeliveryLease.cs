using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261012120000_AddNotificationDeliveryLease")]
public sealed class AddNotificationDeliveryLease : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(AddNotificationDeliveryLease).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Billing.NotificationDeliveryLease.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward migration.");
}
