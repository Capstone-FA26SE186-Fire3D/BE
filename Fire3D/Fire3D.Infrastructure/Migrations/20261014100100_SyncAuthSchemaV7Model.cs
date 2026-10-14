using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fire3D.Infrastructure.Migrations
{
    /// <summary>EF model snapshot for the v7 identity columns; the schema change is applied by 20261014100000_AlignAuthSchemaWithV7.</summary>
    public partial class SyncAuthSchemaV7Model : Migration
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
