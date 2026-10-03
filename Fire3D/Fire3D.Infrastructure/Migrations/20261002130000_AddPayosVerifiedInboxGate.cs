using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
[DbContext(typeof(Fire3DDbContext))][Migration("20261002130000_AddPayosVerifiedInboxGate")]
public sealed class AddPayosVerifiedInboxGate : Migration
{
 protected override void Up(MigrationBuilder builder){using var stream=typeof(AddPayosVerifiedInboxGate).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Billing.PayosWebhook.sql")!;builder.Sql(new StreamReader(stream).ReadToEnd());}
 protected override void Down(MigrationBuilder builder)=>throw new NotSupportedException("Forward migration required.");
}
