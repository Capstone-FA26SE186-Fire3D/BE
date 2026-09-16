using System;
using System.Collections.Generic;
using System.Net;
using Fire3D.Domain.Enums;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fire3D.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordResetTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "public");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:audit_action_enum", "Upload,ConfirmForTraining,Reject,Publish,Revoke,Sync,Login,Logout,Download,Delete,Create,Update,Rollback,Grant,Resume,Payment,Support")
                .Annotation("Npgsql:Enum:feedback_status_enum", "Submitted,Reviewed,Closed")
                .Annotation("Npgsql:Enum:file_type_enum", "IFC")
                .Annotation("Npgsql:Enum:payment_request_status_enum", "Pending,Paid,Expired,Cancelled,Failed")
                .Annotation("Npgsql:Enum:payment_transaction_status_enum", "Received,Verified,Rejected,Applied")
                .Annotation("Npgsql:Enum:processing_step_enum", "Quarantine,Parse,CleanGeometry,Decimate,GenNavMesh,GenHazardGrid,ExportGLB,PackageBundle")
                .Annotation("Npgsql:Enum:processing_step_status_enum", "Started,Success,Failed")
                .Annotation("Npgsql:Enum:quarantine_status_enum", "Pending,Accepted,Rejected")
                .Annotation("Npgsql:Enum:quotation_status_enum", "Draft,Issued,Accepted,Expired,Cancelled")
                .Annotation("Npgsql:Enum:release_status_enum", "Built,Published,Superseded,Revoked")
                .Annotation("Npgsql:Enum:review_action_enum", "ConfirmForTraining,Rejected")
                .Annotation("Npgsql:Enum:revision_status_enum", "Draft,Uploaded,Processing,NeedsFix,ReadyForScenario,ConfirmedForTraining,Rejected,Failed,Superseded")
                .Annotation("Npgsql:Enum:session_mode_enum", "Learn,Guided,Assessment")
                .Annotation("Npgsql:Enum:session_status_enum", "Created,Launching,Running,Completed,CompletedWithSupersededRelease,ScenarioUnsurvivable,Aborted,Abandoned,Crashed")
                .Annotation("Npgsql:Enum:support_priority_enum", "Low,Normal,High,Urgent")
                .Annotation("Npgsql:Enum:support_ticket_status_enum", "Open,InProgress,Resolved,Closed")
                .Annotation("Npgsql:Enum:training_status_enum", "Draft,Active,Closed,Archived")
                .Annotation("Npgsql:Enum:user_role_enum", "PlatformAdmin,OrganizationUser,Trainee")
                .Annotation("Npgsql:PostgresExtension:pgcrypto", ",,");

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_type = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'User'::text"),
                    target_entity = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    old_values = table.Column<string>(type: "jsonb", nullable: true),
                    new_values = table.Column<string>(type: "jsonb", nullable: true),
                    ip_address = table.Column<IPAddress>(type: "inet", nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    action = table.Column<AuditAction>(type: "audit_action_enum", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("audit_logs_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    plan = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValueSql: "'free'::character varying"),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("organizations_pkey", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    role = table.Column<UserRole>(type: "user_role_enum", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    email = table.Column<string>(type: "text", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    full_name = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    last_login_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("users_pkey", x => x.id);
                    table.ForeignKey(
                        name: "users_organization_id_fkey",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "auth_refresh_tokens",
                schema: "public",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_auth_refresh_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "buildings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    building_type = table.Column<string>(type: "text", nullable: true),
                    total_floors = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("buildings_pkey", x => x.id);
                    table.UniqueConstraint("AK_buildings_id_organization_id", x => new { x.id, x.organization_id });
                    table.ForeignKey(
                        name: "buildings_created_by_fkey",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "buildings_organization_id_fkey",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "password_reset_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("password_reset_tokens_pkey", x => x.id);
                    table.ForeignKey(
                        name: "password_reset_tokens_user_id_fkey",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "service_packages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    unit_price = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValueSql: "'VND'::character varying"),
                    duration_months = table.Column<int>(type: "integer", nullable: true),
                    features = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("service_packages_pkey", x => x.id);
                    table.ForeignKey(
                        name: "service_packages_created_by_fkey",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_uuid = table.Column<string>(type: "text", nullable: false),
                    device_model = table.Column<string>(type: "text", nullable: true),
                    os_version = table.Column<string>(type: "text", nullable: true),
                    app_version = table.Column<string>(type: "text", nullable: true),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("user_devices_pkey", x => x.id);
                    table.UniqueConstraint("AK_user_devices_id_user_id", x => new { x.id, x.user_id });
                    table.ForeignKey(
                        name: "user_devices_user_id_fkey",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "building_contacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    building_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contact_name = table.Column<string>(type: "text", nullable: false),
                    contact_role = table.Column<string>(type: "text", nullable: true),
                    phone = table.Column<string>(type: "text", nullable: true),
                    email = table.Column<string>(type: "text", nullable: true),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("building_contacts_pkey", x => x.id);
                    table.ForeignKey(
                        name: "building_contacts_building_id_fkey",
                        column: x => x.building_id,
                        principalTable: "buildings",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "building_floors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    building_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    floor_number = table.Column<int>(type: "integer", nullable: false),
                    floor_name = table.Column<string>(type: "text", nullable: true),
                    floor_plan_url = table.Column<string>(type: "text", nullable: true),
                    area_sqm = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    elevation_meters = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: true),
                    is_basement = table.Column<bool>(type: "boolean", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("building_floors_pkey", x => x.id);
                    table.UniqueConstraint("AK_building_floors_id_building_id", x => new { x.id, x.building_id });
                    table.ForeignKey(
                        name: "building_floors_building_id_organization_id_fkey",
                        columns: x => new { x.building_id, x.organization_id },
                        principalTable: "buildings",
                        principalColumns: new[] { "id", "organization_id" });
                });

            migrationBuilder.CreateTable(
                name: "building_locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    building_id = table.Column<Guid>(type: "uuid", nullable: false),
                    address = table.Column<string>(type: "text", nullable: true),
                    city = table.Column<string>(type: "text", nullable: true),
                    district = table.Column<string>(type: "text", nullable: true),
                    latitude = table.Column<decimal>(type: "numeric(10,8)", precision: 10, scale: 8, nullable: true),
                    longitude = table.Column<decimal>(type: "numeric(11,8)", precision: 11, scale: 8, nullable: true),
                    geojson = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("building_locations_pkey", x => x.id);
                    table.ForeignKey(
                        name: "building_locations_building_id_fkey",
                        column: x => x.building_id,
                        principalTable: "buildings",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    primary_type = table.Column<FileType>(type: "file_type_enum", nullable: false),
                    status = table.Column<RevisionStatus>(type: "revision_status_enum", nullable: false),
                    building_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    version_label = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("revisions_pkey", x => x.id);
                    table.UniqueConstraint("AK_revisions_id_building_id_organization_id", x => new { x.id, x.building_id, x.organization_id });
                    table.ForeignKey(
                        name: "revisions_building_id_organization_id_fkey",
                        columns: x => new { x.building_id, x.organization_id },
                        principalTable: "buildings",
                        principalColumns: new[] { "id", "organization_id" });
                    table.ForeignKey(
                        name: "revisions_uploaded_by_fkey",
                        column: x => x.uploaded_by,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "scenarios",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    building_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("scenarios_pkey", x => x.id);
                    table.UniqueConstraint("AK_scenarios_id_building_id_organization_id", x => new { x.id, x.building_id, x.organization_id });
                    table.ForeignKey(
                        name: "scenarios_building_id_organization_id_fkey",
                        columns: x => new { x.building_id, x.organization_id },
                        principalTable: "buildings",
                        principalColumns: new[] { "id", "organization_id" });
                    table.ForeignKey(
                        name: "scenarios_created_by_fkey",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id");
                },
                comment: "Logical training scenario belonging to a building; versions may pin different compatible building revisions.");

            migrationBuilder.CreateTable(
                name: "quotations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    status = table.Column<QuotationStatus>(type: "quotation_status_enum", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    issued_by = table.Column<Guid>(type: "uuid", nullable: true),
                    quotation_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    unit_price = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    subtotal_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValueSql: "'VND'::character varying"),
                    valid_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    accepted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("quotations_pkey", x => x.id);
                    table.ForeignKey(
                        name: "quotations_issued_by_fkey",
                        column: x => x.issued_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "quotations_organization_id_fkey",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "quotations_requested_by_fkey",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "quotations_service_package_id_fkey",
                        column: x => x.service_package_id,
                        principalTable: "service_packages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "annotation_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    provenance = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'manual'::text"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("annotation_sets_pkey", x => x.id);
                    table.UniqueConstraint("AK_annotation_sets_id_revision_id", x => new { x.id, x.revision_id });
                    table.ForeignKey(
                        name: "annotation_sets_created_by_fkey",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "annotation_sets_revision_id_fkey",
                        column: x => x.revision_id,
                        principalTable: "revisions",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "revision_floors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    building_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    building_floor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ifc_guid = table.Column<string>(type: "text", nullable: false),
                    floor_number = table.Column<int>(type: "integer", nullable: false),
                    floor_name = table.Column<string>(type: "text", nullable: false),
                    elevation_meters = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    coordinate_transform = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("revision_floors_pkey", x => x.id);
                    table.ForeignKey(
                        name: "revision_floors_building_floor_id_building_id_fkey",
                        columns: x => new { x.building_floor_id, x.building_id },
                        principalTable: "building_floors",
                        principalColumns: new[] { "id", "building_id" });
                    table.ForeignKey(
                        name: "revision_floors_revision_id_building_id_organization_id_fkey",
                        columns: x => new { x.revision_id, x.building_id, x.organization_id },
                        principalTable: "revisions",
                        principalColumns: new[] { "id", "building_id", "organization_id" });
                },
                comment: "Immutable floor snapshot used by content, QR labels and historical replay; changes require a new revision.");

            migrationBuilder.CreateTable(
                name: "source_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    file_type = table.Column<FileType>(type: "file_type_enum", nullable: false),
                    quarantine_status = table.Column<QuarantineStatus>(type: "quarantine_status_enum", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    original_filename = table.Column<string>(type: "text", nullable: false),
                    file_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    storage_url = table.Column<string>(type: "text", nullable: false),
                    mime_type = table.Column<string>(type: "text", nullable: false),
                    sha256_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    quarantine_note = table.Column<string>(type: "text", nullable: true),
                    usage_rights = table.Column<string>(type: "text", nullable: false),
                    source_tool = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("source_documents_pkey", x => x.id);
                    table.UniqueConstraint("AK_source_documents_id_revision_id", x => new { x.id, x.revision_id });
                    table.ForeignKey(
                        name: "source_documents_revision_id_fkey",
                        column: x => x.revision_id,
                        principalTable: "revisions",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "source_documents_uploaded_by_fkey",
                        column: x => x.uploaded_by,
                        principalTable: "users",
                        principalColumn: "id");
                },
                comment: "Phase 1: one accepted source IFC per revision; storage_url is a private stable object key, never a presigned URL.");

            migrationBuilder.CreateTable(
                name: "scenario_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    scenario_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    building_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'1.0'::text"),
                    algorithm_version = table.Column<string>(type: "text", nullable: false),
                    random_seed = table.Column<long>(type: "bigint", nullable: false),
                    time_limit_seconds = table.Column<int>(type: "integer", nullable: false),
                    spawn_config = table.Column<string>(type: "jsonb", nullable: false),
                    goal_config = table.Column<string>(type: "jsonb", nullable: false),
                    fire_source_config = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    npc_config = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    blocked_elements = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    routing_config = table.Column<string>(type: "jsonb", nullable: false),
                    scoring_config = table.Column<string>(type: "jsonb", nullable: false),
                    mode_policy = table.Column<string>(type: "jsonb", nullable: false),
                    safety_thresholds = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    replan_interval_seconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 5),
                    scenario_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("scenario_versions_pkey", x => x.id);
                    table.UniqueConstraint("AK_scenario_versions_id_revision_id_organization_id", x => new { x.id, x.revision_id, x.organization_id });
                    table.ForeignKey(
                        name: "scenario_versions_created_by_fkey",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "scenario_versions_revision_id_building_id_organization_id_fkey",
                        columns: x => new { x.revision_id, x.building_id, x.organization_id },
                        principalTable: "revisions",
                        principalColumns: new[] { "id", "building_id", "organization_id" });
                    table.ForeignKey(
                        name: "scenario_versions_scenario_id_building_id_organization_id_fkey",
                        columns: x => new { x.scenario_id, x.building_id, x.organization_id },
                        principalTable: "scenarios",
                        principalColumns: new[] { "id", "building_id", "organization_id" });
                },
                comment: "Append-only snapshot. Confirmation is per version through revision_reviews, not an exclusive lock on all scenarios of the revision.");

            migrationBuilder.CreateTable(
                name: "processing_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'Geometry'::text"),
                    scenario_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    job_key = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'Queued'::text"),
                    attempt_number = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    toolchain_version = table.Column<string>(type: "text", nullable: false),
                    lease_owner = table.Column<string>(type: "text", nullable: true),
                    heartbeat_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("processing_jobs_pkey", x => x.id);
                    table.UniqueConstraint("AK_processing_jobs_id_revision_id", x => new { x.id, x.revision_id });
                    table.ForeignKey(
                        name: "processing_jobs_revision_id_fkey",
                        column: x => x.revision_id,
                        principalTable: "revisions",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "processing_jobs_scenario_version_id_fkey",
                        column: x => x.scenario_version_id,
                        principalTable: "scenario_versions",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "processing_jobs_source_document_id_revision_id_fkey",
                        columns: x => new { x.source_document_id, x.revision_id },
                        principalTable: "source_documents",
                        principalColumns: new[] { "id", "revision_id" });
                });

            migrationBuilder.CreateTable(
                name: "revision_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    artifact_type = table.Column<string>(type: "text", nullable: false),
                    object_key = table.Column<string>(type: "text", nullable: false),
                    sha256_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    schema_version = table.Column<string>(type: "text", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("revision_artifacts_pkey", x => x.id);
                    table.UniqueConstraint("AK_revision_artifacts_id_revision_id", x => new { x.id, x.revision_id });
                    table.ForeignKey(
                        name: "revision_artifacts_job_id_revision_id_fkey",
                        columns: x => new { x.job_id, x.revision_id },
                        principalTable: "processing_jobs",
                        principalColumns: new[] { "id", "revision_id" });
                    table.ForeignKey(
                        name: "revision_artifacts_revision_id_fkey",
                        column: x => x.revision_id,
                        principalTable: "revisions",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "revision_processing_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    step = table.Column<ProcessingStep>(type: "processing_step_enum", nullable: false),
                    status = table.Column<ProcessingStepStatus>(type: "processing_step_status_enum", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message = table.Column<string>(type: "text", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: true),
                    attempt_number = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    logged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("revision_processing_logs_pkey", x => x.id);
                    table.ForeignKey(
                        name: "revision_processing_logs_job_id_revision_id_fkey",
                        columns: x => new { x.job_id, x.revision_id },
                        principalTable: "processing_jobs",
                        principalColumns: new[] { "id", "revision_id" });
                    table.ForeignKey(
                        name: "revision_processing_logs_revision_id_fkey",
                        column: x => x.revision_id,
                        principalTable: "revisions",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "validation_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    scenario_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    candidate_artifact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    annotation_set_id = table.Column<Guid>(type: "uuid", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    scenario_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    validator_version = table.Column<string>(type: "text", nullable: false),
                    report = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("validation_runs_pkey", x => x.id);
                    table.ForeignKey(
                        name: "validation_runs_annotation_set_id_revision_id_fkey",
                        columns: x => new { x.annotation_set_id, x.revision_id },
                        principalTable: "annotation_sets",
                        principalColumns: new[] { "id", "revision_id" });
                    table.ForeignKey(
                        name: "validation_runs_candidate_artifact_id_revision_id_fkey",
                        columns: x => new { x.candidate_artifact_id, x.revision_id },
                        principalTable: "revision_artifacts",
                        principalColumns: new[] { "id", "revision_id" });
                    table.ForeignKey(
                        name: "validation_runs_job_id_revision_id_fkey",
                        columns: x => new { x.job_id, x.revision_id },
                        principalTable: "processing_jobs",
                        principalColumns: new[] { "id", "revision_id" });
                    table.ForeignKey(
                        name: "validation_runs_revision_id_fkey",
                        column: x => x.revision_id,
                        principalTable: "revisions",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "validation_runs_scenario_version_id_fkey",
                        column: x => x.scenario_version_id,
                        principalTable: "scenario_versions",
                        principalColumn: "id");
                },
                comment: "Append-only attestation from a trusted validator; SQL checks bindings, not geometry, routing or cryptographic file contents.");

            migrationBuilder.CreateTable(
                name: "revision_issues",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    validation_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    ifc_guid = table.Column<string>(type: "text", nullable: true),
                    message = table.Column<string>(type: "text", nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("revision_issues_pkey", x => x.id);
                    table.ForeignKey(
                        name: "revision_issues_validation_run_id_fkey",
                        column: x => x.validation_run_id,
                        principalTable: "validation_runs",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "revision_reviews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    action = table.Column<ReviewAction>(type: "review_action_enum", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    validation_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    annotation_set_id = table.Column<Guid>(type: "uuid", nullable: true),
                    review_message = table.Column<string>(type: "text", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("revision_reviews_pkey", x => x.id);
                    table.ForeignKey(
                        name: "revision_reviews_annotation_set_id_revision_id_fkey",
                        columns: x => new { x.annotation_set_id, x.revision_id },
                        principalTable: "annotation_sets",
                        principalColumns: new[] { "id", "revision_id" });
                    table.ForeignKey(
                        name: "revision_reviews_reviewed_by_fkey",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "revision_reviews_revision_id_fkey",
                        column: x => x.revision_id,
                        principalTable: "revisions",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "revision_reviews_scenario_version_id_fkey",
                        column: x => x.scenario_version_id,
                        principalTable: "scenario_versions",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "revision_reviews_validation_run_id_fkey",
                        column: x => x.validation_run_id,
                        principalTable: "validation_runs",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "releases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    status = table.Column<ReleaseStatus>(type: "release_status_enum", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    building_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    confirmation_review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    published_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    safety_thresholds = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    revoked_reason = table.Column<string>(type: "text", nullable: true),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("releases_pkey", x => x.id);
                    table.UniqueConstraint("AK_releases_id_scenario_version_id_organization_id", x => new { x.id, x.scenario_version_id, x.organization_id });
                    table.ForeignKey(
                        name: "releases_confirmation_review_id_fkey",
                        column: x => x.confirmation_review_id,
                        principalTable: "revision_reviews",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "releases_published_by_fkey",
                        column: x => x.published_by,
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "releases_revision_id_building_id_organization_id_fkey",
                        columns: x => new { x.revision_id, x.building_id, x.organization_id },
                        principalTable: "revisions",
                        principalColumns: new[] { "id", "building_id", "organization_id" });
                    table.ForeignKey(
                        name: "releases_revoked_by_fkey",
                        column: x => x.revoked_by,
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "releases_scenario_version_id_revision_id_organization_id_fkey",
                        columns: x => new { x.scenario_version_id, x.revision_id, x.organization_id },
                        principalTable: "scenario_versions",
                        principalColumns: new[] { "id", "revision_id", "organization_id" });
                });

            migrationBuilder.CreateTable(
                name: "release_packages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    candidate_artifact_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_url = table.Column<string>(type: "text", nullable: false),
                    manifest_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    package_url = table.Column<string>(type: "text", nullable: false),
                    checksum_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    package_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    min_runtime_version = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'1.0'::text"),
                    build_target = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'Android'::text"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("release_packages_pkey", x => x.id);
                    table.ForeignKey(
                        name: "release_packages_candidate_artifact_id_fkey",
                        column: x => x.candidate_artifact_id,
                        principalTable: "revision_artifacts",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "release_packages_release_id_fkey",
                        column: x => x.release_id,
                        principalTable: "releases",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "trainings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    status = table.Column<TrainingStatus>(type: "training_status_enum", nullable: false),
                    mode = table.Column<SessionMode>(type: "session_mode_enum", nullable: false),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    start_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    end_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    allowed_modes = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "ARRAY['Learn'::text, 'Guided'::text, 'Assessment'::text]"),
                    max_attempts = table.Column<int>(type: "integer", nullable: true, comment: "NULL = unlimited. Positive value limits Assessment session creation per Trainee and Training, including launch failures; Learn/Guided unlimited."),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("trainings_pkey", x => x.id);
                    table.UniqueConstraint("AK_trainings_id_release_id_organization_id", x => new { x.id, x.release_id, x.organization_id });
                    table.UniqueConstraint("AK_trainings_id_release_id_scenario_version_id_organization_id", x => new { x.id, x.release_id, x.scenario_version_id, x.organization_id });
                    table.ForeignKey(
                        name: "trainings_created_by_fkey",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "trainings_release_id_scenario_version_id_organization_id_fkey",
                        columns: x => new { x.release_id, x.scenario_version_id, x.organization_id },
                        principalTable: "releases",
                        principalColumns: new[] { "id", "scenario_version_id", "organization_id" });
                });

            migrationBuilder.CreateTable(
                name: "release_qr_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    training_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_floor_id = table.Column<Guid>(type: "uuid", nullable: true, comment: "Placement metadata only; does not override the immutable scenario spawn."),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    qr_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, comment: "SHA-256 of a high-entropy opaque token. Original token returned once for printing; rotation creates a new row."),
                    label = table.Column<string>(type: "text", nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("release_qr_codes_pkey", x => x.id);
                    table.UniqueConstraint("AK_release_qr_codes_id_training_id_release_id_organization_id", x => new { x.id, x.training_id, x.release_id, x.organization_id });
                    table.ForeignKey(
                        name: "release_qr_codes_created_by_fkey",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "release_qr_codes_revision_floor_id_fkey",
                        column: x => x.revision_floor_id,
                        principalTable: "revision_floors",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "release_qr_codes_training_id_release_id_organization_id_fkey",
                        columns: x => new { x.training_id, x.release_id, x.organization_id },
                        principalTable: "trainings",
                        principalColumns: new[] { "id", "release_id", "organization_id" });
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    mode = table.Column<SessionMode>(type: "session_mode_enum", nullable: false),
                    status = table.Column<SessionStatus>(type: "session_status_enum", nullable: false),
                    content_status_at_completion = table.Column<ReleaseStatus>(type: "release_status_enum", nullable: true),
                    training_id = table.Column<Guid>(type: "uuid", nullable: false),
                    release_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trainee_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    qr_code_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_key = table.Column<Guid>(type: "uuid", nullable: false),
                    app_version = table.Column<string>(type: "text", nullable: false),
                    unity_version = table.Column<string>(type: "text", nullable: false),
                    protocol_version = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'1.0'::text"),
                    scenario_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    release_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    launched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    terminal_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("sessions_pkey", x => x.id);
                    table.ForeignKey(
                        name: "sessions_device_id_trainee_user_id_fkey",
                        columns: x => new { x.device_id, x.trainee_user_id },
                        principalTable: "user_devices",
                        principalColumns: new[] { "id", "user_id" });
                    table.ForeignKey(
                        name: "sessions_qr_code_id_training_id_release_id_organization_id_fkey",
                        columns: x => new { x.qr_code_id, x.training_id, x.release_id, x.organization_id },
                        principalTable: "release_qr_codes",
                        principalColumns: new[] { "id", "training_id", "release_id", "organization_id" });
                    table.ForeignKey(
                        name: "sessions_trainee_user_id_fkey",
                        column: x => x.trainee_user_id,
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "sessions_training_id_release_id_scenario_version_id_organi_fkey",
                        columns: x => new { x.training_id, x.release_id, x.scenario_version_id, x.organization_id },
                        principalTable: "trainings",
                        principalColumns: new[] { "id", "release_id", "scenario_version_id", "organization_id" });
                });

            migrationBuilder.CreateTable(
                name: "feedback",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    status = table.Column<FeedbackStatus>(type: "feedback_status_enum", nullable: false),
                    submitted_by = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    rating = table.Column<int>(type: "integer", nullable: true),
                    message = table.Column<string>(type: "text", nullable: false),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("feedback_pkey", x => x.id);
                    table.ForeignKey(
                        name: "feedback_organization_id_fkey",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "feedback_reviewed_by_fkey",
                        column: x => x.reviewed_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "feedback_session_id_fkey",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "feedback_submitted_by_fkey",
                        column: x => x.submitted_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "session_checkpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence_number = table.Column<int>(type: "integer", nullable: false),
                    player_transform = table.Column<string>(type: "jsonb", nullable: false),
                    player_status = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    world_interactive_states = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    hazard_time_step = table.Column<int>(type: "integer", nullable: false),
                    npc_states = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    active_objectives = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    release_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    scenario_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("session_checkpoints_pkey", x => x.id);
                    table.ForeignKey(
                        name: "session_checkpoints_session_id_fkey",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "session_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence_number = table.Column<long>(type: "bigint", nullable: false),
                    client_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schema_version = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'1.0'::text"),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    event_data = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    elapsed_ms = table.Column<long>(type: "bigint", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("session_events_pkey", x => x.id);
                    table.ForeignKey(
                        name: "session_events_session_id_fkey",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "session_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    completion_key = table.Column<Guid>(type: "uuid", nullable: false),
                    last_event_sequence = table.Column<long>(type: "bigint", nullable: false),
                    submission_payload = table.Column<string>(type: "jsonb", nullable: false),
                    result_schema_version = table.Column<string>(type: "text", nullable: false, defaultValueSql: "'1.0'::text"),
                    rubric_version = table.Column<string>(type: "text", nullable: false),
                    score = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    time_taken_seconds = table.Column<int>(type: "integer", nullable: false),
                    wrong_exits = table.Column<int>(type: "integer", nullable: false),
                    hazard_exposure_score = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    total_distance_meters = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    reached_exit = table.Column<bool>(type: "boolean", nullable: false),
                    exit_point_id = table.Column<string>(type: "text", nullable: true),
                    path_traveled = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    client_started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    client_ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("session_results_pkey", x => x.id);
                    table.UniqueConstraint("AK_session_results_session_id", x => x.session_id);
                    table.ForeignKey(
                        name: "session_results_session_id_fkey",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id");
                },
                comment: "One immutable server-accepted result per session. Backend validates/calculates rubric before insert; no client score trust implied.");

            migrationBuilder.CreateTable(
                name: "support_tickets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    priority = table.Column<SupportPriority>(type: "support_priority_enum", nullable: false),
                    status = table.Column<SupportTicketStatus>(type: "support_ticket_status_enum", nullable: false),
                    ticket_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    feedback_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_to = table.Column<Guid>(type: "uuid", nullable: true),
                    subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("support_tickets_pkey", x => x.id);
                    table.ForeignKey(
                        name: "support_tickets_assigned_to_fkey",
                        column: x => x.assigned_to,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "support_tickets_created_by_fkey",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "support_tickets_feedback_id_fkey",
                        column: x => x.feedback_id,
                        principalTable: "feedback",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "support_tickets_organization_id_fkey",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "support_tickets_session_id_fkey",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "debrief_artifacts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trajectory_heatmap = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    optimal_path = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    wrong_decisions = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    hazard_timeline = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    npc_summary = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    is_visible_to_trainee = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    generator_version = table.Column<string>(type: "text", nullable: false),
                    generated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("debrief_artifacts_pkey", x => x.id);
                    table.ForeignKey(
                        name: "debrief_artifacts_session_id_fkey",
                        column: x => x.session_id,
                        principalTable: "session_results",
                        principalColumn: "session_id");
                });

            migrationBuilder.CreateTable(
                name: "invoice_metadata",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    payment_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quotation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    legal_name = table.Column<string>(type: "text", nullable: false),
                    tax_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    billing_address = table.Column<string>(type: "text", nullable: true),
                    subtotal_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValueSql: "'VND'::character varying"),
                    issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    invoice_url = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("invoice_metadata_pkey", x => x.id);
                    table.ForeignKey(
                        name: "invoice_metadata_organization_id_fkey",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "invoice_metadata_quotation_id_fkey",
                        column: x => x.quotation_id,
                        principalTable: "quotations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    status = table.Column<PaymentTransactionStatus>(type: "payment_transaction_status_enum", nullable: false),
                    payment_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    webhook_event_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    provider_transaction_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    received_order_code = table.Column<long>(type: "bigint", nullable: false),
                    received_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    received_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    signature_verified = table.Column<bool>(type: "boolean", nullable: false),
                    raw_payload = table.Column<string>(type: "jsonb", nullable: false),
                    rejection_reason = table.Column<string>(type: "text", nullable: true),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    signature_verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("payment_transactions_pkey", x => x.id);
                    table.UniqueConstraint("AK_payment_transactions_id_payment_request_id", x => new { x.id, x.payment_request_id });
                });

            migrationBuilder.CreateTable(
                name: "payos_payment_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    status = table.Column<PaymentRequestStatus>(type: "payment_request_status_enum", nullable: false),
                    quotation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    order_code = table.Column<long>(type: "bigint", nullable: false),
                    expected_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    expected_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValueSql: "'VND'::character varying"),
                    checkout_url = table.Column<string>(type: "text", nullable: false),
                    return_url = table.Column<string>(type: "text", nullable: false),
                    cancel_url = table.Column<string>(type: "text", nullable: false),
                    paid_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    paid_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("payos_payment_requests_pkey", x => x.id);
                    table.ForeignKey(
                        name: "fk_payos_paid_transaction_for_request",
                        columns: x => new { x.paid_transaction_id, x.id },
                        principalTable: "payment_transactions",
                        principalColumns: new[] { "id", "payment_request_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "payos_payment_requests_organization_id_fkey",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "payos_payment_requests_quotation_id_fkey",
                        column: x => x.quotation_id,
                        principalTable: "quotations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "payos_payment_requests_requested_by_fkey",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "annotation_sets_id_revision_id_key",
                table: "annotation_sets",
                columns: new[] { "id", "revision_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "annotation_sets_revision_id_version_number_key",
                table: "annotation_sets",
                columns: new[] { "revision_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_annotation_sets_created_by",
                table: "annotation_sets",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "audit_org_history",
                table: "audit_logs",
                columns: new[] { "organization_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_auth_refresh_tokens_expires_at",
                schema: "public",
                table: "auth_refresh_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_auth_refresh_tokens_family_id",
                schema: "public",
                table: "auth_refresh_tokens",
                column: "family_id",
                unique: true,
                filter: "consumed_at IS NULL AND revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_auth_refresh_tokens_token_hash",
                schema: "public",
                table: "auth_refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_auth_refresh_tokens_user_id_family_id",
                schema: "public",
                table: "auth_refresh_tokens",
                columns: new[] { "user_id", "family_id" });

            migrationBuilder.CreateIndex(
                name: "one_primary_contact",
                table: "building_contacts",
                column: "building_id",
                unique: true,
                filter: "is_primary");

            migrationBuilder.CreateIndex(
                name: "building_floors_building_id_floor_number_key",
                table: "building_floors",
                columns: new[] { "building_id", "floor_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "building_floors_id_building_id_key",
                table: "building_floors",
                columns: new[] { "id", "building_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_building_floors_building_id_organization_id",
                table: "building_floors",
                columns: new[] { "building_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "building_locations_building_id_key",
                table: "building_locations",
                column: "building_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "buildings_id_organization_id_key",
                table: "buildings",
                columns: new[] { "id", "organization_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_buildings_created_by",
                table: "buildings",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_buildings_organization_id",
                table: "buildings",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "debrief_artifacts_session_id_key",
                table: "debrief_artifacts",
                column: "session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_feedback_organization",
                table: "feedback",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "idx_feedback_reviewed_by",
                table: "feedback",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "idx_feedback_session",
                table: "feedback",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "idx_feedback_submitted_by",
                table: "feedback",
                column: "submitted_by");

            migrationBuilder.CreateIndex(
                name: "idx_invoice_metadata_organization",
                table: "invoice_metadata",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "idx_invoice_metadata_quotation",
                table: "invoice_metadata",
                column: "quotation_id");

            migrationBuilder.CreateIndex(
                name: "invoice_metadata_invoice_number_key",
                table: "invoice_metadata",
                column: "invoice_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "invoice_metadata_payment_transaction_id_key",
                table: "invoice_metadata",
                column: "payment_transaction_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "organizations_slug_key",
                table: "organizations",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_password_reset_tokens_user_id",
                table: "password_reset_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "idx_payment_transactions_request",
                table: "payment_transactions",
                column: "payment_request_id");

            migrationBuilder.CreateIndex(
                name: "payment_transactions_id_payment_request_id_key",
                table: "payment_transactions",
                columns: new[] { "id", "payment_request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "payment_transactions_provider_transaction_id_key",
                table: "payment_transactions",
                column: "provider_transaction_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "payment_transactions_webhook_event_id_key",
                table: "payment_transactions",
                column: "webhook_event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_payos_payment_requests_organization",
                table: "payos_payment_requests",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "idx_payos_payment_requests_quotation",
                table: "payos_payment_requests",
                column: "quotation_id");

            migrationBuilder.CreateIndex(
                name: "idx_payos_payment_requests_requested_by",
                table: "payos_payment_requests",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "IX_payos_payment_requests_paid_transaction_id_id",
                table: "payos_payment_requests",
                columns: new[] { "paid_transaction_id", "id" });

            migrationBuilder.CreateIndex(
                name: "payos_payment_requests_order_code_key",
                table: "payos_payment_requests",
                column: "order_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "payos_payment_requests_paid_transaction_id_key",
                table: "payos_payment_requests",
                column: "paid_transaction_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_processing_jobs_scenario_version_id",
                table: "processing_jobs",
                column: "scenario_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_processing_jobs_source_document_id_revision_id",
                table: "processing_jobs",
                columns: new[] { "source_document_id", "revision_id" });

            migrationBuilder.CreateIndex(
                name: "one_live_revision_job",
                table: "processing_jobs",
                column: "revision_id",
                unique: true,
                filter: "(status = ANY (ARRAY['Queued'::text, 'Running'::text]))");

            migrationBuilder.CreateIndex(
                name: "processing_jobs_id_revision_id_key",
                table: "processing_jobs",
                columns: new[] { "id", "revision_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "processing_jobs_job_key_key",
                table: "processing_jobs",
                column: "job_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_quotations_issued_by",
                table: "quotations",
                column: "issued_by");

            migrationBuilder.CreateIndex(
                name: "idx_quotations_organization",
                table: "quotations",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "idx_quotations_requested_by",
                table: "quotations",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "idx_quotations_service_package",
                table: "quotations",
                column: "service_package_id");

            migrationBuilder.CreateIndex(
                name: "quotations_quotation_number_key",
                table: "quotations",
                column: "quotation_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_release_packages_candidate_artifact_id",
                table: "release_packages",
                column: "candidate_artifact_id");

            migrationBuilder.CreateIndex(
                name: "release_packages_release_id_key",
                table: "release_packages",
                column: "release_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_release_qr_codes_created_by",
                table: "release_qr_codes",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_release_qr_codes_revision_floor_id",
                table: "release_qr_codes",
                column: "revision_floor_id");

            migrationBuilder.CreateIndex(
                name: "IX_release_qr_codes_training_id_release_id_organization_id",
                table: "release_qr_codes",
                columns: new[] { "training_id", "release_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "qr_release",
                table: "release_qr_codes",
                column: "release_id");

            migrationBuilder.CreateIndex(
                name: "qr_training",
                table: "release_qr_codes",
                column: "training_id");

            migrationBuilder.CreateIndex(
                name: "release_qr_codes_id_training_id_release_id_organization_id_key",
                table: "release_qr_codes",
                columns: new[] { "id", "training_id", "release_id", "organization_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "release_qr_codes_qr_hash_key",
                table: "release_qr_codes",
                column: "qr_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_releases_confirmation_review_id",
                table: "releases",
                column: "confirmation_review_id");

            migrationBuilder.CreateIndex(
                name: "IX_releases_published_by",
                table: "releases",
                column: "published_by");

            migrationBuilder.CreateIndex(
                name: "IX_releases_revision_id_building_id_organization_id",
                table: "releases",
                columns: new[] { "revision_id", "building_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_releases_revoked_by",
                table: "releases",
                column: "revoked_by");

            migrationBuilder.CreateIndex(
                name: "IX_releases_scenario_version_id_revision_id_organization_id",
                table: "releases",
                columns: new[] { "scenario_version_id", "revision_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "releases_id_scenario_version_id_organization_id_key",
                table: "releases",
                columns: new[] { "id", "scenario_version_id", "organization_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "releases_revision_id_scenario_version_id_key",
                table: "releases",
                columns: new[] { "revision_id", "scenario_version_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "artifacts_revision",
                table: "revision_artifacts",
                column: "revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_revision_artifacts_job_id_revision_id",
                table: "revision_artifacts",
                columns: new[] { "job_id", "revision_id" });

            migrationBuilder.CreateIndex(
                name: "revision_artifacts_id_revision_id_key",
                table: "revision_artifacts",
                columns: new[] { "id", "revision_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "revision_artifacts_job_id_artifact_type_sha256_hash_key",
                table: "revision_artifacts",
                columns: new[] { "job_id", "artifact_type", "sha256_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_revision_floors_building_floor_id_building_id",
                table: "revision_floors",
                columns: new[] { "building_floor_id", "building_id" });

            migrationBuilder.CreateIndex(
                name: "IX_revision_floors_revision_id_building_id_organization_id",
                table: "revision_floors",
                columns: new[] { "revision_id", "building_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "revision_floors_id_revision_id_key",
                table: "revision_floors",
                columns: new[] { "id", "revision_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "revision_floors_revision_id_building_floor_id_key",
                table: "revision_floors",
                columns: new[] { "revision_id", "building_floor_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "revision_floors_revision_id_ifc_guid_key",
                table: "revision_floors",
                columns: new[] { "revision_id", "ifc_guid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_revision_issues_validation_run_id",
                table: "revision_issues",
                column: "validation_run_id");

            migrationBuilder.CreateIndex(
                name: "IX_revision_processing_logs_job_id_revision_id",
                table: "revision_processing_logs",
                columns: new[] { "job_id", "revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_revision_processing_logs_revision_id",
                table: "revision_processing_logs",
                column: "revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_revision_reviews_annotation_set_id_revision_id",
                table: "revision_reviews",
                columns: new[] { "annotation_set_id", "revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_revision_reviews_reviewed_by",
                table: "revision_reviews",
                column: "reviewed_by");

            migrationBuilder.CreateIndex(
                name: "IX_revision_reviews_revision_id",
                table: "revision_reviews",
                column: "revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_revision_reviews_validation_run_id",
                table: "revision_reviews",
                column: "validation_run_id");

            migrationBuilder.CreateIndex(
                name: "one_scenario_confirmation",
                table: "revision_reviews",
                column: "scenario_version_id",
                unique: true,
                filter: "(action = 'ConfirmForTraining'::review_action_enum)");

            migrationBuilder.CreateIndex(
                name: "IX_revisions_building_id_organization_id",
                table: "revisions",
                columns: new[] { "building_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_revisions_uploaded_by",
                table: "revisions",
                column: "uploaded_by");

            migrationBuilder.CreateIndex(
                name: "revisions_building_id_version_label_key",
                table: "revisions",
                columns: new[] { "building_id", "version_label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "revisions_id_building_id_organization_id_key",
                table: "revisions",
                columns: new[] { "id", "building_id", "organization_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "revisions_id_organization_id_key",
                table: "revisions",
                columns: new[] { "id", "organization_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_scenario_versions_created_by",
                table: "scenario_versions",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_scenario_versions_revision_id_building_id_organization_id",
                table: "scenario_versions",
                columns: new[] { "revision_id", "building_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_scenario_versions_scenario_id_building_id_organization_id",
                table: "scenario_versions",
                columns: new[] { "scenario_id", "building_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "scenario_versions_id_revision_id_organization_id_key",
                table: "scenario_versions",
                columns: new[] { "id", "revision_id", "organization_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "scenario_versions_scenario_id_version_number_key",
                table: "scenario_versions",
                columns: new[] { "scenario_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_scenarios_building_id_organization_id",
                table: "scenarios",
                columns: new[] { "building_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_scenarios_created_by",
                table: "scenarios",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "scenarios_id_building_id_organization_id_key",
                table: "scenarios",
                columns: new[] { "id", "building_id", "organization_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_service_packages_active",
                table: "service_packages",
                column: "code",
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "idx_service_packages_created_by",
                table: "service_packages",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "service_packages_code_key",
                table: "service_packages",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "session_checkpoints_session_id_sequence_number_key",
                table: "session_checkpoints",
                columns: new[] { "session_id", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "session_events_session_id_client_event_id_key",
                table: "session_events",
                columns: new[] { "session_id", "client_event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "session_events_session_id_sequence_number_key",
                table: "session_events",
                columns: new[] { "session_id", "sequence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "session_results_session_id_key",
                table: "session_results",
                column: "session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_device_id_trainee_user_id",
                table: "sessions",
                columns: new[] { "device_id", "trainee_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_qr_code_id_training_id_release_id_organization_id",
                table: "sessions",
                columns: new[] { "qr_code_id", "training_id", "release_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_training_id_release_id_scenario_version_id_organiz~",
                table: "sessions",
                columns: new[] { "training_id", "release_id", "scenario_version_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "sessions_org_history",
                table: "sessions",
                columns: new[] { "organization_id", "started_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "sessions_personal_history",
                table: "sessions",
                columns: new[] { "trainee_user_id", "started_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "sessions_release",
                table: "sessions",
                column: "release_id");

            migrationBuilder.CreateIndex(
                name: "sessions_trainee_user_id_start_key_key",
                table: "sessions",
                columns: new[] { "trainee_user_id", "start_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_source_documents_uploaded_by",
                table: "source_documents",
                column: "uploaded_by");

            migrationBuilder.CreateIndex(
                name: "source_documents_id_revision_id_key",
                table: "source_documents",
                columns: new[] { "id", "revision_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "source_documents_revision_id_key",
                table: "source_documents",
                column: "revision_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_support_tickets_assigned_to",
                table: "support_tickets",
                column: "assigned_to");

            migrationBuilder.CreateIndex(
                name: "idx_support_tickets_created_by",
                table: "support_tickets",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "idx_support_tickets_feedback",
                table: "support_tickets",
                column: "feedback_id");

            migrationBuilder.CreateIndex(
                name: "idx_support_tickets_organization",
                table: "support_tickets",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "idx_support_tickets_session",
                table: "support_tickets",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "support_tickets_ticket_number_key",
                table: "support_tickets",
                column: "ticket_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_trainings_created_by",
                table: "trainings",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "IX_trainings_release_id_scenario_version_id_organization_id",
                table: "trainings",
                columns: new[] { "release_id", "scenario_version_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "trainings_id_release_id_organization_id_key",
                table: "trainings",
                columns: new[] { "id", "release_id", "organization_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "trainings_id_release_id_scenario_version_id_organization_id_key",
                table: "trainings",
                columns: new[] { "id", "release_id", "scenario_version_id", "organization_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "trainings_release",
                table: "trainings",
                column: "release_id");

            migrationBuilder.CreateIndex(
                name: "user_devices_id_user_id_key",
                table: "user_devices",
                columns: new[] { "id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "user_devices_user_id_device_uuid_key",
                table: "user_devices",
                columns: new[] { "user_id", "device_uuid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_organization_id",
                table: "users",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "users_email_key",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_validation_runs_annotation_set_id_revision_id",
                table: "validation_runs",
                columns: new[] { "annotation_set_id", "revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_validation_runs_candidate_artifact_id_revision_id",
                table: "validation_runs",
                columns: new[] { "candidate_artifact_id", "revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_validation_runs_job_id_revision_id",
                table: "validation_runs",
                columns: new[] { "job_id", "revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_validation_runs_scenario_version_id",
                table: "validation_runs",
                column: "scenario_version_id");

            migrationBuilder.CreateIndex(
                name: "validation_revision",
                table: "validation_runs",
                columns: new[] { "revision_id", "kind", "outcome" });

            migrationBuilder.CreateIndex(
                name: "validation_runs_job_id_key",
                table: "validation_runs",
                column: "job_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "invoice_metadata_payment_transaction_id_fkey",
                table: "invoice_metadata",
                column: "payment_transaction_id",
                principalTable: "payment_transactions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "payment_transactions_payment_request_id_fkey",
                table: "payment_transactions",
                column: "payment_request_id",
                principalTable: "payos_payment_requests",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "payos_payment_requests_requested_by_fkey",
                table: "payos_payment_requests");

            migrationBuilder.DropForeignKey(
                name: "quotations_issued_by_fkey",
                table: "quotations");

            migrationBuilder.DropForeignKey(
                name: "quotations_requested_by_fkey",
                table: "quotations");

            migrationBuilder.DropForeignKey(
                name: "service_packages_created_by_fkey",
                table: "service_packages");

            migrationBuilder.DropForeignKey(
                name: "payos_payment_requests_organization_id_fkey",
                table: "payos_payment_requests");

            migrationBuilder.DropForeignKey(
                name: "quotations_organization_id_fkey",
                table: "quotations");

            migrationBuilder.DropForeignKey(
                name: "fk_payos_paid_transaction_for_request",
                table: "payos_payment_requests");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "auth_refresh_tokens",
                schema: "public");

            migrationBuilder.DropTable(
                name: "building_contacts");

            migrationBuilder.DropTable(
                name: "building_locations");

            migrationBuilder.DropTable(
                name: "debrief_artifacts");

            migrationBuilder.DropTable(
                name: "invoice_metadata");

            migrationBuilder.DropTable(
                name: "password_reset_tokens");

            migrationBuilder.DropTable(
                name: "release_packages");

            migrationBuilder.DropTable(
                name: "revision_issues");

            migrationBuilder.DropTable(
                name: "revision_processing_logs");

            migrationBuilder.DropTable(
                name: "session_checkpoints");

            migrationBuilder.DropTable(
                name: "session_events");

            migrationBuilder.DropTable(
                name: "support_tickets");

            migrationBuilder.DropTable(
                name: "session_results");

            migrationBuilder.DropTable(
                name: "feedback");

            migrationBuilder.DropTable(
                name: "sessions");

            migrationBuilder.DropTable(
                name: "user_devices");

            migrationBuilder.DropTable(
                name: "release_qr_codes");

            migrationBuilder.DropTable(
                name: "revision_floors");

            migrationBuilder.DropTable(
                name: "trainings");

            migrationBuilder.DropTable(
                name: "building_floors");

            migrationBuilder.DropTable(
                name: "releases");

            migrationBuilder.DropTable(
                name: "revision_reviews");

            migrationBuilder.DropTable(
                name: "validation_runs");

            migrationBuilder.DropTable(
                name: "annotation_sets");

            migrationBuilder.DropTable(
                name: "revision_artifacts");

            migrationBuilder.DropTable(
                name: "processing_jobs");

            migrationBuilder.DropTable(
                name: "scenario_versions");

            migrationBuilder.DropTable(
                name: "source_documents");

            migrationBuilder.DropTable(
                name: "scenarios");

            migrationBuilder.DropTable(
                name: "revisions");

            migrationBuilder.DropTable(
                name: "buildings");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "organizations");

            migrationBuilder.DropTable(
                name: "payment_transactions");

            migrationBuilder.DropTable(
                name: "payos_payment_requests");

            migrationBuilder.DropTable(
                name: "quotations");

            migrationBuilder.DropTable(
                name: "service_packages");
        }
    }
}
