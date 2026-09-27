using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fire3D.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAvatarStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "avatar_storage_key",
                table: "users",
                type: "text",
                nullable: true);

            // Local-password support predates EF migration tracking and may already have created this column.
            // Keep this idempotent so existing accounts are preserved during the additive avatar rollout.
            migrationBuilder.Sql("ALTER TABLE public.users ADD COLUMN IF NOT EXISTS password_hash text;");

            migrationBuilder.AddColumn<long>(
                name: "profile_revision",
                table: "users",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "avatar_upload_intents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staging_object_key = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    expected_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_avatar_upload_intents", x => x.id);
                    table.ForeignKey(
                        name: "FK_avatar_upload_intents_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_avatar_upload_intents_user_id_expires_at",
                table: "avatar_upload_intents",
                columns: new[] { "user_id", "expires_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "avatar_upload_intents");

            migrationBuilder.DropColumn(
                name: "avatar_storage_key",
                table: "users");

            // password_hash belongs to the earlier local-auth rollout and must never be removed by an avatar rollback.

            migrationBuilder.DropColumn(
                name: "profile_revision",
                table: "users");
        }
    }
}
