using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fire3D.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SyncPayosCheckoutModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Applied additively by AddPayosCheckoutGates.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Forward migration required.");
        }
    }
}
