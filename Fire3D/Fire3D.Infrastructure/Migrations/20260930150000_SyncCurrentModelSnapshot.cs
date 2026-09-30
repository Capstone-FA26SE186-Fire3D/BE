using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fire3D.Infrastructure.Migrations;

/// <summary>
/// Synchronizes EF's model snapshot after the preceding hand-written, additive migrations.
/// The schema changes themselves are owned by those migrations, so this migration is deliberately a no-op.
/// </summary>
public partial class SyncCurrentModelSnapshot : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
