using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fire3D.Infrastructure.Migrations
{
    /// <summary>EF model snapshot for the enterprise workflow columns; the schema change is applied by 20261012130000_AddEnterpriseWorkflow.</summary>
    public partial class SyncEnterpriseWorkflowModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Use a reviewed forward migration.");
        }
    }
}