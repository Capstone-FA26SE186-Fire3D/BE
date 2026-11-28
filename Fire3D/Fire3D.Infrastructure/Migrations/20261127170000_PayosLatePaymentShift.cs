using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
/// <summary>BE#241: PayOS money verified after the planned service start provisions the period from the payment time.</summary>
[DbContext(typeof(Fire3DDbContext))]
[Migration("20261127170000_PayosLatePaymentShift")]
public sealed class PayosLatePaymentShift : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream=typeof(PayosLatePaymentShift).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Billing.PayosLatePaymentShift.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Use a reviewed forward migration.");
}
