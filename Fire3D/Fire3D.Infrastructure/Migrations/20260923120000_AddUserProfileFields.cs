using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fire3D.Infrastructure.Migrations;

[DbContext(typeof(Fire3DDbContext))]
[Migration("20260923120000_AddUserProfileFields")]
public partial class AddUserProfileFields : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "avatar_url", table: "users", type: "character varying(2048)", maxLength: 2048, nullable: true);
        migrationBuilder.AddColumn<DateOnly>(name: "dob", table: "users", type: "date", nullable: true);
        migrationBuilder.AddColumn<string>(name: "gender", table: "users", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(name: "phone_number", table: "users", type: "character varying(32)", maxLength: 32, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "avatar_url", table: "users");
        migrationBuilder.DropColumn(name: "dob", table: "users");
        migrationBuilder.DropColumn(name: "gender", table: "users");
        migrationBuilder.DropColumn(name: "phone_number", table: "users");
    }
}
