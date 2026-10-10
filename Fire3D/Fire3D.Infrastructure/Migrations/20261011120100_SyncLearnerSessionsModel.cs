using Microsoft.EntityFrameworkCore.Migrations;
namespace Fire3D.Infrastructure.Migrations;
public partial class SyncLearnerSessionsModel : Migration
{
    // DDL is applied by AddLearnerSessions; this migration records the generated EF model.
    protected override void Up(MigrationBuilder builder) { }
    protected override void Down(MigrationBuilder builder) => throw new NotSupportedException("Preserve learner history; use a reviewed forward migration.");
}
