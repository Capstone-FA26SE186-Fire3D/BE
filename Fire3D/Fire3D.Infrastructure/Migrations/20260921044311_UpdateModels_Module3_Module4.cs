using System;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fire3D.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateModels_Module3_Module4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "password_hash",
                table: "users");

            migrationBuilder.DropColumn(
                name: "attempt_number",
                table: "processing_jobs");

            migrationBuilder.RenameColumn(
                name: "attempt_number",
                table: "revision_processing_logs",
                newName: "AttemptNumber");

            migrationBuilder.RenameColumn(
                name: "toolchain_version",
                table: "processing_jobs",
                newName: "input_hash");

            migrationBuilder.AddColumn<string>(
                name: "firebase_uid",
                table: "users",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fcm_token",
                table: "user_devices",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "AttemptNumber",
                table: "revision_processing_logs",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 1);

            migrationBuilder.CreateTable(
                name: "runtime_compatibility_catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    runtime_version = table.Column<string>(type: "text", nullable: false),
                    protocol_version = table.Column<string>(type: "text", nullable: false),
                    manifest_schema_version = table.Column<string>(type: "text", nullable: false),
                    capabilities = table.Column<JsonNode>(type: "jsonb", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_compatibility_catalog", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scenario_drafts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    building_id = table.Column<Guid>(type: "uuid", nullable: false),
                    draft_number = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<JsonNode>(type: "jsonb", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    last_ai_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scenario_drafts", x => x.id);
                    table.ForeignKey(
                        name: "FK_scenario_drafts_buildings_building_id",
                        column: x => x.building_id,
                        principalTable: "buildings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_scenario_drafts_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_scenario_drafts_revisions_revision_id",
                        column: x => x.revision_id,
                        principalTable: "revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_scenario_drafts_scenarios_scenario_id",
                        column: x => x.scenario_id,
                        principalTable: "scenarios",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_scenario_drafts_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "playtest_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    building_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario_draft_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scenario_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_entitlement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    package_hash = table.Column<string>(type: "text", nullable: false),
                    protocol_version = table.Column<string>(type: "text", nullable: false),
                    manifest_schema_version = table.Column<string>(type: "text", nullable: false),
                    prepare_idempotency_key = table.Column<string>(type: "text", nullable: true),
                    runtime_version = table.Column<string>(type: "text", nullable: true),
                    start_idempotency_key = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    completion_idempotency_key = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playtest_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_playtest_sessions_buildings_building_id",
                        column: x => x.building_id,
                        principalTable: "buildings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_playtest_sessions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_playtest_sessions_revisions_revision_id",
                        column: x => x.revision_id,
                        principalTable: "revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_playtest_sessions_scenario_drafts_scenario_draft_id",
                        column: x => x.scenario_draft_id,
                        principalTable: "scenario_drafts",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_playtest_sessions_scenario_versions_scenario_version_id",
                        column: x => x.scenario_version_id,
                        principalTable: "scenario_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_playtest_sessions_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_playtest_sessions_building_id",
                table: "playtest_sessions",
                column: "building_id");

            migrationBuilder.CreateIndex(
                name: "IX_playtest_sessions_created_by",
                table: "playtest_sessions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_playtest_sessions_organization_id",
                table: "playtest_sessions",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_playtest_sessions_revision_id",
                table: "playtest_sessions",
                column: "revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_playtest_sessions_scenario_draft_id",
                table: "playtest_sessions",
                column: "scenario_draft_id");

            migrationBuilder.CreateIndex(
                name: "IX_playtest_sessions_scenario_version_id",
                table: "playtest_sessions",
                column: "scenario_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_scenario_drafts_building_id",
                table: "scenario_drafts",
                column: "building_id");

            migrationBuilder.CreateIndex(
                name: "IX_scenario_drafts_created_by",
                table: "scenario_drafts",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_scenario_drafts_organization_id",
                table: "scenario_drafts",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_scenario_drafts_revision_id",
                table: "scenario_drafts",
                column: "revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_scenario_drafts_scenario_id",
                table: "scenario_drafts",
                column: "scenario_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "playtest_sessions");

            migrationBuilder.DropTable(
                name: "runtime_compatibility_catalog");

            migrationBuilder.DropTable(
                name: "scenario_drafts");

            migrationBuilder.DropColumn(
                name: "firebase_uid",
                table: "users");

            migrationBuilder.DropColumn(
                name: "fcm_token",
                table: "user_devices");

            migrationBuilder.RenameColumn(
                name: "AttemptNumber",
                table: "revision_processing_logs",
                newName: "attempt_number");

            migrationBuilder.RenameColumn(
                name: "input_hash",
                table: "processing_jobs",
                newName: "toolchain_version");

            migrationBuilder.AddColumn<string>(
                name: "password_hash",
                table: "users",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<int>(
                name: "attempt_number",
                table: "revision_processing_logs",
                type: "integer",
                nullable: false,
                defaultValue: 1,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "attempt_number",
                table: "processing_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }
    }
}
