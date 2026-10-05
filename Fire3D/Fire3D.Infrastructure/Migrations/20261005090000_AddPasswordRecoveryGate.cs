using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20261005090000_AddPasswordRecoveryGate")]
public sealed class AddPasswordRecoveryGate : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        using var stream = typeof(AddPasswordRecoveryGate).Assembly.GetManifestResourceStream("Fire3D.Infrastructure.Persistence.PasswordRecoveryGate.sql")!;
        builder.Sql(new StreamReader(stream).ReadToEnd());
    }
    protected override void Down(MigrationBuilder builder) =>
        throw new NotSupportedException("Use a reviewed forward migration for password recovery permissions.");
}
