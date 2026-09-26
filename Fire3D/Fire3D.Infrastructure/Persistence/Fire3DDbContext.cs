using System;
using System.Collections.Generic;
using Fire3D.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Persistence;

public partial class Fire3DDbContext : DbContext
{
    public Fire3DDbContext(DbContextOptions<Fire3DDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<AnnotationSet> AnnotationSets { get; set; }

    public virtual DbSet<AuditLog> AuditLogs { get; set; }

    public virtual DbSet<Building> Buildings { get; set; }

    public virtual DbSet<BuildingContact> BuildingContacts { get; set; }

    public virtual DbSet<BuildingFloor> BuildingFloors { get; set; }

    public virtual DbSet<BuildingLocation> BuildingLocations { get; set; }

    public virtual DbSet<DebriefArtifact> DebriefArtifacts { get; set; }

    public virtual DbSet<Feedback> Feedbacks { get; set; }

    public virtual DbSet<InvoiceMetadatum> InvoiceMetadata { get; set; }

    public virtual DbSet<Organization> Organizations { get; set; }

    public virtual DbSet<PaymentTransaction> PaymentTransactions { get; set; }

    public virtual DbSet<PayosPaymentRequest> PayosPaymentRequests { get; set; }

    public virtual DbSet<ProcessingJob> ProcessingJobs { get; set; }

    public virtual DbSet<Quotation> Quotations { get; set; }

    public virtual DbSet<Release> Releases { get; set; }

    public virtual DbSet<ReleasePackage> ReleasePackages { get; set; }

    public virtual DbSet<ReleaseQrCode> ReleaseQrCodes { get; set; }

    public virtual DbSet<Revision> Revisions { get; set; }

    public virtual DbSet<RevisionArtifact> RevisionArtifacts { get; set; }

    public virtual DbSet<RevisionFloor> RevisionFloors { get; set; }

    public virtual DbSet<RevisionIssue> RevisionIssues { get; set; }

    public virtual DbSet<RevisionProcessingLog> RevisionProcessingLogs { get; set; }

    public virtual DbSet<RevisionReview> RevisionReviews { get; set; }

    public virtual DbSet<Scenario> Scenarios { get; set; }

    public virtual DbSet<ScenarioVersion> ScenarioVersions { get; set; }

    public virtual DbSet<ScenarioDraft> ScenarioDrafts { get; set; }

    public virtual DbSet<PlaytestSession> PlaytestSessions { get; set; }

    public virtual DbSet<RuntimeCompatibilityCatalog> RuntimeCompatibilityCatalogs { get; set; }

    public virtual DbSet<ServicePackage> ServicePackages { get; set; }

    public virtual DbSet<Session> Sessions { get; set; }

    public virtual DbSet<SessionCheckpoint> SessionCheckpoints { get; set; }

    public virtual DbSet<SessionEvent> SessionEvents { get; set; }

    public virtual DbSet<SessionResult> SessionResults { get; set; }

    public virtual DbSet<SourceDocument> SourceDocuments { get; set; }

    public virtual DbSet<SupportTicket> SupportTickets { get; set; }

    public virtual DbSet<Training> Trainings { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<UserDevice> UserDevices { get; set; }

    public virtual DbSet<AvatarUploadIntent> AvatarUploadIntents { get; set; }

    public virtual DbSet<PasswordResetToken> PasswordResetTokens { get; set; }

    public virtual DbSet<ValidationRun> ValidationRuns { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasPostgresEnum("audit_action_enum", new[] { "Upload", "ConfirmForTraining", "Reject", "Publish", "Revoke", "Sync", "Login", "Logout", "Download", "Delete", "Create", "Update", "Rollback", "Grant", "Resume", "Payment", "Support" })
            .HasPostgresEnum("feedback_status_enum", new[] { "Submitted", "Reviewed", "Closed" })
            .HasPostgresEnum("file_type_enum", new[] { "IFC" })
            .HasPostgresEnum("payment_request_status_enum", new[] { "Pending", "Paid", "Expired", "Cancelled", "Failed" })
            .HasPostgresEnum("payment_transaction_status_enum", new[] { "Received", "Verified", "Rejected", "Applied" })
            .HasPostgresEnum("processing_step_enum", new[] { "Quarantine", "Parse", "CleanGeometry", "Decimate", "GenNavMesh", "GenHazardGrid", "ExportGLB", "PackageBundle" })
            .HasPostgresEnum("processing_step_status_enum", new[] { "Started", "Success", "Failed" })
            .HasPostgresEnum("quarantine_status_enum", new[] { "Pending", "Accepted", "Rejected" })
            .HasPostgresEnum("quotation_status_enum", new[] { "Draft", "Issued", "Accepted", "Expired", "Cancelled" })
            .HasPostgresEnum("release_status_enum", new[] { "Built", "Published", "Superseded", "Revoked" })
            .HasPostgresEnum("review_action_enum", new[] { "ConfirmForTraining", "Rejected" })
            .HasPostgresEnum("revision_status_enum", new[] { "Draft", "Uploaded", "Processing", "NeedsFix", "ReadyForScenario", "ConfirmedForTraining", "Rejected", "Failed", "Superseded" })
            .HasPostgresEnum("session_mode_enum", new[] { "Learn", "Guided", "Assessment" })
            .HasPostgresEnum("session_status_enum", new[] { "Created", "Launching", "Running", "Completed", "CompletedWithSupersededRelease", "ScenarioUnsurvivable", "Aborted", "Abandoned", "Crashed" })
            .HasPostgresEnum("support_priority_enum", new[] { "Low", "Normal", "High", "Urgent" })
            .HasPostgresEnum("support_ticket_status_enum", new[] { "Open", "InProgress", "Resolved", "Closed" })
            .HasPostgresEnum("training_status_enum", new[] { "Draft", "Active", "Closed", "Archived" })
            .HasPostgresEnum("user_role_enum", new[] { "PlatformAdmin", "OrganizationUser", "Trainee" })
            .HasPostgresExtension("pgcrypto");

        modelBuilder.Entity<AnnotationSet>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("annotation_sets_pkey");

            entity.ToTable("annotation_sets");

            entity.HasIndex(e => new { e.Id, e.RevisionId }, "annotation_sets_id_revision_id_key").IsUnique();

            entity.HasIndex(e => new { e.RevisionId, e.VersionNumber }, "annotation_sets_revision_id_version_number_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Data)
                .HasColumnType("jsonb")
                .HasColumnName("data");
            entity.Property(e => e.Provenance)
                .HasDefaultValueSql("'manual'::text")
                .HasColumnName("provenance");
            entity.Property(e => e.RevisionId).HasColumnName("revision_id");
            entity.Property(e => e.VersionNumber).HasColumnName("version_number");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.AnnotationSets)
                .HasForeignKey(d => d.CreatedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("annotation_sets_created_by_fkey");

            entity.HasOne(d => d.Revision).WithMany(p => p.AnnotationSets)
                .HasForeignKey(d => d.RevisionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("annotation_sets_revision_id_fkey");
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("audit_logs_pkey");

            entity.ToTable("audit_logs");

            entity.HasIndex(e => new { e.OrganizationId, e.CreatedAt }, "audit_org_history").IsDescending(false, true);

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.ActorType)
                .HasDefaultValueSql("'User'::text")
                .HasColumnName("actor_type");
            entity.Property(e => e.CorrelationId)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("correlation_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.IpAddress).HasColumnName("ip_address");
            entity.Property(e => e.NewValues)
                .HasColumnType("jsonb")
                .HasColumnName("new_values");
            entity.Property(e => e.OldValues)
                .HasColumnType("jsonb")
                .HasColumnName("old_values");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.TargetEntity).HasColumnName("target_entity");
            entity.Property(e => e.TargetId).HasColumnName("target_id");
            entity.Property(e => e.UserAgent).HasColumnName("user_agent");
            entity.Property(e => e.UserId).HasColumnName("user_id");
        });

        modelBuilder.Entity<Building>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("buildings_pkey");

            entity.ToTable("buildings");

            entity.HasIndex(e => new { e.Id, e.OrganizationId }, "buildings_id_organization_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.BuildingType).HasColumnName("building_type");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.TotalFloors)
                .HasDefaultValue(1)
                .HasColumnName("total_floors");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.Buildings)
                .HasForeignKey(d => d.CreatedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("buildings_created_by_fkey");

            entity.HasOne(d => d.Organization).WithMany(p => p.Buildings)
                .HasForeignKey(d => d.OrganizationId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("buildings_organization_id_fkey");
        });

        modelBuilder.Entity<BuildingContact>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("building_contacts_pkey");

            entity.ToTable("building_contacts");

            entity.HasIndex(e => e.BuildingId, "one_primary_contact")
                .IsUnique()
                .HasFilter("is_primary");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.BuildingId).HasColumnName("building_id");
            entity.Property(e => e.ContactName).HasColumnName("contact_name");
            entity.Property(e => e.ContactRole).HasColumnName("contact_role");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.Email).HasColumnName("email");
            entity.Property(e => e.IsPrimary).HasColumnName("is_primary");
            entity.Property(e => e.Phone).HasColumnName("phone");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Building).WithOne(p => p.BuildingContact)
                .HasForeignKey<BuildingContact>(d => d.BuildingId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("building_contacts_building_id_fkey");
        });

        modelBuilder.Entity<BuildingFloor>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("building_floors_pkey");

            entity.ToTable("building_floors");

            entity.HasIndex(e => new { e.BuildingId, e.FloorNumber }, "building_floors_building_id_floor_number_key").IsUnique();

            entity.HasIndex(e => new { e.Id, e.BuildingId }, "building_floors_id_building_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AreaSqm)
                .HasPrecision(10, 2)
                .HasColumnName("area_sqm");
            entity.Property(e => e.BuildingId).HasColumnName("building_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.ElevationMeters)
                .HasPrecision(10, 3)
                .HasColumnName("elevation_meters");
            entity.Property(e => e.FloorName).HasColumnName("floor_name");
            entity.Property(e => e.FloorNumber).HasColumnName("floor_number");
            entity.Property(e => e.FloorPlanUrl).HasColumnName("floor_plan_url");
            entity.Property(e => e.IsBasement).HasColumnName("is_basement");
            entity.Property(e => e.Metadata)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("metadata");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Building).WithMany(p => p.BuildingFloors)
                .HasPrincipalKey(p => new { p.Id, p.OrganizationId })
                .HasForeignKey(d => new { d.BuildingId, d.OrganizationId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("building_floors_building_id_organization_id_fkey");
        });

        modelBuilder.Entity<BuildingLocation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("building_locations_pkey");

            entity.ToTable("building_locations");

            entity.HasIndex(e => e.BuildingId, "building_locations_building_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.Address).HasColumnName("address");
            entity.Property(e => e.BuildingId).HasColumnName("building_id");
            entity.Property(e => e.City).HasColumnName("city");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.District).HasColumnName("district");
            entity.Property(e => e.Geojson)
                .HasColumnType("jsonb")
                .HasColumnName("geojson");
            entity.Property(e => e.Latitude)
                .HasPrecision(10, 8)
                .HasColumnName("latitude");
            entity.Property(e => e.Longitude)
                .HasPrecision(11, 8)
                .HasColumnName("longitude");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Building).WithOne(p => p.BuildingLocation)
                .HasForeignKey<BuildingLocation>(d => d.BuildingId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("building_locations_building_id_fkey");
        });

        modelBuilder.Entity<DebriefArtifact>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("debrief_artifacts_pkey");

            entity.ToTable("debrief_artifacts");

            entity.HasIndex(e => e.SessionId, "debrief_artifacts_session_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.GeneratedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("generated_at");
            entity.Property(e => e.GeneratorVersion).HasColumnName("generator_version");
            entity.Property(e => e.HazardTimeline)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("hazard_timeline");
            entity.Property(e => e.IsVisibleToTrainee)
                .HasDefaultValue(true)
                .HasColumnName("is_visible_to_trainee");
            entity.Property(e => e.NpcSummary)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("npc_summary");
            entity.Property(e => e.OptimalPath)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("optimal_path");
            entity.Property(e => e.SessionId).HasColumnName("session_id");
            entity.Property(e => e.TrajectoryHeatmap)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("trajectory_heatmap");
            entity.Property(e => e.WrongDecisions)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("wrong_decisions");

            entity.HasOne(d => d.Session).WithOne(p => p.DebriefArtifact)
                .HasPrincipalKey<SessionResult>(p => p.SessionId)
                .HasForeignKey<DebriefArtifact>(d => d.SessionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("debrief_artifacts_session_id_fkey");
        });

        modelBuilder.Entity<Feedback>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("feedback_pkey");

            entity.ToTable("feedback");

            entity.HasIndex(e => e.OrganizationId, "idx_feedback_organization");

            entity.HasIndex(e => e.ReviewedBy, "idx_feedback_reviewed_by");

            entity.HasIndex(e => e.SessionId, "idx_feedback_session");

            entity.HasIndex(e => e.SubmittedBy, "idx_feedback_submitted_by");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.Category)
                .HasMaxLength(100)
                .HasColumnName("category");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.Message).HasColumnName("message");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.Rating).HasColumnName("rating");
            entity.Property(e => e.ReviewedAt).HasColumnName("reviewed_at");
            entity.Property(e => e.ReviewedBy).HasColumnName("reviewed_by");
            entity.Property(e => e.SessionId).HasColumnName("session_id");
            entity.Property(e => e.SubmittedBy).HasColumnName("submitted_by");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Organization).WithMany(p => p.Feedbacks)
                .HasForeignKey(d => d.OrganizationId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("feedback_organization_id_fkey");

            entity.HasOne(d => d.ReviewedByNavigation).WithMany(p => p.FeedbackReviewedByNavigations)
                .HasForeignKey(d => d.ReviewedBy)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("feedback_reviewed_by_fkey");

            entity.HasOne(d => d.Session).WithMany(p => p.Feedbacks)
                .HasForeignKey(d => d.SessionId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("feedback_session_id_fkey");

            entity.HasOne(d => d.SubmittedByNavigation).WithMany(p => p.FeedbackSubmittedByNavigations)
                .HasForeignKey(d => d.SubmittedBy)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("feedback_submitted_by_fkey");
        });

        modelBuilder.Entity<InvoiceMetadatum>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("invoice_metadata_pkey");

            entity.ToTable("invoice_metadata");

            entity.HasIndex(e => e.OrganizationId, "idx_invoice_metadata_organization");

            entity.HasIndex(e => e.QuotationId, "idx_invoice_metadata_quotation");

            entity.HasIndex(e => e.InvoiceNumber, "invoice_metadata_invoice_number_key").IsUnique();

            entity.HasIndex(e => e.PaymentTransactionId, "invoice_metadata_payment_transaction_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.BillingAddress).HasColumnName("billing_address");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.Currency)
                .HasMaxLength(3)
                .HasDefaultValueSql("'VND'::character varying")
                .HasColumnName("currency");
            entity.Property(e => e.InvoiceNumber)
                .HasMaxLength(100)
                .HasColumnName("invoice_number");
            entity.Property(e => e.InvoiceUrl).HasColumnName("invoice_url");
            entity.Property(e => e.IssuedAt).HasColumnName("issued_at");
            entity.Property(e => e.LegalName).HasColumnName("legal_name");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.PaymentTransactionId).HasColumnName("payment_transaction_id");
            entity.Property(e => e.QuotationId).HasColumnName("quotation_id");
            entity.Property(e => e.SubtotalAmount)
                .HasPrecision(14, 2)
                .HasColumnName("subtotal_amount");
            entity.Property(e => e.TaxAmount)
                .HasPrecision(14, 2)
                .HasColumnName("tax_amount");
            entity.Property(e => e.TaxCode)
                .HasMaxLength(50)
                .HasColumnName("tax_code");
            entity.Property(e => e.TotalAmount)
                .HasPrecision(14, 2)
                .HasColumnName("total_amount");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Organization).WithMany(p => p.InvoiceMetadata)
                .HasForeignKey(d => d.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("invoice_metadata_organization_id_fkey");

            entity.HasOne(d => d.PaymentTransaction).WithOne(p => p.InvoiceMetadatum)
                .HasForeignKey<InvoiceMetadatum>(d => d.PaymentTransactionId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("invoice_metadata_payment_transaction_id_fkey");

            entity.HasOne(d => d.Quotation).WithMany(p => p.InvoiceMetadata)
                .HasForeignKey(d => d.QuotationId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("invoice_metadata_quotation_id_fkey");
        });

        modelBuilder.Entity<Organization>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("organizations_pkey");

            entity.ToTable("organizations");

            entity.HasIndex(e => e.Slug, "organizations_slug_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.Metadata)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("metadata");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.Plan)
                .HasMaxLength(50)
                .HasDefaultValueSql("'free'::character varying")
                .HasColumnName("plan");
            entity.Property(e => e.Slug).HasColumnName("slug");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<PaymentTransaction>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("payment_transactions_pkey");

            entity.ToTable("payment_transactions");

            entity.HasIndex(e => e.PaymentRequestId, "idx_payment_transactions_request");

            entity.HasIndex(e => new { e.Id, e.PaymentRequestId }, "payment_transactions_id_payment_request_id_key").IsUnique();

            entity.HasIndex(e => e.ProviderTransactionId, "payment_transactions_provider_transaction_id_key").IsUnique();

            entity.HasIndex(e => e.WebhookEventId, "payment_transactions_webhook_event_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.PaymentRequestId).HasColumnName("payment_request_id");
            entity.Property(e => e.ProcessedAt).HasColumnName("processed_at");
            entity.Property(e => e.ProviderTransactionId)
                .HasMaxLength(255)
                .HasColumnName("provider_transaction_id");
            entity.Property(e => e.RawPayload)
                .HasColumnType("jsonb")
                .HasColumnName("raw_payload");
            entity.Property(e => e.ReceivedAmount)
                .HasPrecision(14, 2)
                .HasColumnName("received_amount");
            entity.Property(e => e.ReceivedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("received_at");
            entity.Property(e => e.ReceivedCurrency)
                .HasMaxLength(3)
                .HasColumnName("received_currency");
            entity.Property(e => e.ReceivedOrderCode).HasColumnName("received_order_code");
            entity.Property(e => e.RejectionReason).HasColumnName("rejection_reason");
            entity.Property(e => e.SignatureVerified).HasColumnName("signature_verified");
            entity.Property(e => e.SignatureVerifiedAt).HasColumnName("signature_verified_at");
            entity.Property(e => e.WebhookEventId)
                .HasMaxLength(255)
                .HasColumnName("webhook_event_id");

            entity.HasOne(d => d.PaymentRequest).WithMany(p => p.PaymentTransactions)
                .HasForeignKey(d => d.PaymentRequestId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("payment_transactions_payment_request_id_fkey");
        });

        modelBuilder.Entity<PayosPaymentRequest>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("payos_payment_requests_pkey");

            entity.ToTable("payos_payment_requests");

            entity.HasIndex(e => e.OrganizationId, "idx_payos_payment_requests_organization");

            entity.HasIndex(e => e.QuotationId, "idx_payos_payment_requests_quotation");

            entity.HasIndex(e => e.RequestedBy, "idx_payos_payment_requests_requested_by");

            entity.HasIndex(e => e.OrderCode, "payos_payment_requests_order_code_key").IsUnique();

            entity.HasIndex(e => e.PaidTransactionId, "payos_payment_requests_paid_transaction_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CancelUrl).HasColumnName("cancel_url");
            entity.Property(e => e.CheckoutUrl).HasColumnName("checkout_url");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.ExpectedAmount)
                .HasPrecision(14, 2)
                .HasColumnName("expected_amount");
            entity.Property(e => e.ExpectedCurrency)
                .HasMaxLength(3)
                .HasDefaultValueSql("'VND'::character varying")
                .HasColumnName("expected_currency");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.OrderCode).HasColumnName("order_code");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.PaidAt).HasColumnName("paid_at");
            entity.Property(e => e.PaidTransactionId).HasColumnName("paid_transaction_id");
            entity.Property(e => e.QuotationId).HasColumnName("quotation_id");
            entity.Property(e => e.RequestedBy).HasColumnName("requested_by");
            entity.Property(e => e.ReturnUrl).HasColumnName("return_url");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Organization).WithMany(p => p.PayosPaymentRequests)
                .HasForeignKey(d => d.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("payos_payment_requests_organization_id_fkey");

            entity.HasOne(d => d.Quotation).WithMany(p => p.PayosPaymentRequests)
                .HasForeignKey(d => d.QuotationId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("payos_payment_requests_quotation_id_fkey");

            entity.HasOne(d => d.RequestedByNavigation).WithMany(p => p.PayosPaymentRequests)
                .HasForeignKey(d => d.RequestedBy)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("payos_payment_requests_requested_by_fkey");

            entity.HasOne(d => d.PaymentTransaction).WithMany(p => p.PayosPaymentRequests)
                .HasPrincipalKey(p => new { p.Id, p.PaymentRequestId })
                .HasForeignKey(d => new { d.PaidTransactionId, d.Id })
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("fk_payos_paid_transaction_for_request");
        });

        modelBuilder.Entity<ProcessingJob>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("processing_jobs_pkey");

            entity.ToTable("processing_jobs");

            entity.HasIndex(e => e.RevisionId, "one_live_revision_job")
                .IsUnique()
                .HasFilter("(status = ANY (ARRAY['Queued'::text, 'Running'::text]))");

            entity.HasIndex(e => new { e.Id, e.RevisionId }, "processing_jobs_id_revision_id_key").IsUnique();

            entity.HasIndex(e => e.JobKey, "processing_jobs_job_key_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.ErrorMessage).HasColumnName("error_message");
            entity.Property(e => e.FinishedAt).HasColumnName("finished_at");
            entity.Property(e => e.HeartbeatAt).HasColumnName("heartbeat_at");
            entity.Property(e => e.JobKey).HasColumnName("job_key");
            entity.Property(e => e.Kind)
                .HasDefaultValueSql("'Geometry'::text")
                .HasColumnName("kind");
            entity.Property(e => e.LeaseOwner).HasColumnName("lease_owner");
            entity.Property(e => e.RevisionId).HasColumnName("revision_id");
            entity.Property(e => e.ScenarioVersionId).HasColumnName("scenario_version_id");
            entity.Property(e => e.SourceDocumentId).HasColumnName("source_document_id");
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.Status)
                .HasDefaultValueSql("'Queued'::text")
                .HasColumnName("status");
            entity.Property(e => e.InputHash).HasColumnName("input_hash");

            entity.HasOne(d => d.Revision).WithOne(p => p.ProcessingJob)
                .HasForeignKey<ProcessingJob>(d => d.RevisionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("processing_jobs_revision_id_fkey");

            entity.HasOne(d => d.ScenarioVersion).WithMany(p => p.ProcessingJobs)
                .HasForeignKey(d => d.ScenarioVersionId)
                .HasConstraintName("processing_jobs_scenario_version_id_fkey");

            entity.HasOne(d => d.SourceDocument).WithMany(p => p.ProcessingJobs)
                .HasPrincipalKey(p => new { p.Id, p.RevisionId })
                .HasForeignKey(d => new { d.SourceDocumentId, d.RevisionId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("processing_jobs_source_document_id_revision_id_fkey");
        });

        modelBuilder.Entity<Quotation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("quotations_pkey");

            entity.ToTable("quotations");

            entity.HasIndex(e => e.IssuedBy, "idx_quotations_issued_by");

            entity.HasIndex(e => e.OrganizationId, "idx_quotations_organization");

            entity.HasIndex(e => e.RequestedBy, "idx_quotations_requested_by");

            entity.HasIndex(e => e.ServicePackageId, "idx_quotations_service_package");

            entity.HasIndex(e => e.QuotationNumber, "quotations_quotation_number_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AcceptedAt).HasColumnName("accepted_at");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.Currency)
                .HasMaxLength(3)
                .HasDefaultValueSql("'VND'::character varying")
                .HasColumnName("currency");
            entity.Property(e => e.DiscountAmount)
                .HasPrecision(14, 2)
                .HasColumnName("discount_amount");
            entity.Property(e => e.IssuedAt).HasColumnName("issued_at");
            entity.Property(e => e.IssuedBy).HasColumnName("issued_by");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.Quantity)
                .HasDefaultValue(1)
                .HasColumnName("quantity");
            entity.Property(e => e.QuotationNumber)
                .HasMaxLength(50)
                .HasColumnName("quotation_number");
            entity.Property(e => e.RequestedBy).HasColumnName("requested_by");
            entity.Property(e => e.ServicePackageId).HasColumnName("service_package_id");
            entity.Property(e => e.SubtotalAmount)
                .HasPrecision(14, 2)
                .HasColumnName("subtotal_amount");
            entity.Property(e => e.TaxAmount)
                .HasPrecision(14, 2)
                .HasColumnName("tax_amount");
            entity.Property(e => e.TotalAmount)
                .HasPrecision(14, 2)
                .HasColumnName("total_amount");
            entity.Property(e => e.UnitPrice)
                .HasPrecision(14, 2)
                .HasColumnName("unit_price");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.ValidUntil).HasColumnName("valid_until");

            entity.HasOne(d => d.IssuedByNavigation).WithMany(p => p.QuotationIssuedByNavigations)
                .HasForeignKey(d => d.IssuedBy)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("quotations_issued_by_fkey");

            entity.HasOne(d => d.Organization).WithMany(p => p.Quotations)
                .HasForeignKey(d => d.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("quotations_organization_id_fkey");

            entity.HasOne(d => d.RequestedByNavigation).WithMany(p => p.QuotationRequestedByNavigations)
                .HasForeignKey(d => d.RequestedBy)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("quotations_requested_by_fkey");

            entity.HasOne(d => d.ServicePackage).WithMany(p => p.Quotations)
                .HasForeignKey(d => d.ServicePackageId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("quotations_service_package_id_fkey");
        });

        modelBuilder.Entity<Release>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("releases_pkey");

            entity.ToTable("releases");

            entity.HasIndex(e => new { e.Id, e.ScenarioVersionId, e.OrganizationId }, "releases_id_scenario_version_id_organization_id_key").IsUnique();

            entity.HasIndex(e => new { e.RevisionId, e.ScenarioVersionId }, "releases_revision_id_scenario_version_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.BuildingId).HasColumnName("building_id");
            entity.Property(e => e.ConfirmationReviewId).HasColumnName("confirmation_review_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.PublishedAt).HasColumnName("published_at");
            entity.Property(e => e.PublishedBy).HasColumnName("published_by");
            entity.Property(e => e.RevisionId).HasColumnName("revision_id");
            entity.Property(e => e.RevokedAt).HasColumnName("revoked_at");
            entity.Property(e => e.RevokedBy).HasColumnName("revoked_by");
            entity.Property(e => e.RevokedReason).HasColumnName("revoked_reason");
            entity.Property(e => e.SafetyThresholds)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("safety_thresholds");
            entity.Property(e => e.ScenarioVersionId).HasColumnName("scenario_version_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.ConfirmationReview).WithMany(p => p.Releases)
                .HasForeignKey(d => d.ConfirmationReviewId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("releases_confirmation_review_id_fkey");

            entity.HasOne(d => d.PublishedByNavigation).WithMany(p => p.ReleasePublishedByNavigations)
                .HasForeignKey(d => d.PublishedBy)
                .HasConstraintName("releases_published_by_fkey");

            entity.HasOne(d => d.RevokedByNavigation).WithMany(p => p.ReleaseRevokedByNavigations)
                .HasForeignKey(d => d.RevokedBy)
                .HasConstraintName("releases_revoked_by_fkey");

            entity.HasOne(d => d.Revision).WithMany(p => p.Releases)
                .HasPrincipalKey(p => new { p.Id, p.BuildingId, p.OrganizationId })
                .HasForeignKey(d => new { d.RevisionId, d.BuildingId, d.OrganizationId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("releases_revision_id_building_id_organization_id_fkey");

            entity.HasOne(d => d.ScenarioVersion).WithMany(p => p.Releases)
                .HasPrincipalKey(p => new { p.Id, p.RevisionId, p.OrganizationId })
                .HasForeignKey(d => new { d.ScenarioVersionId, d.RevisionId, d.OrganizationId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("releases_scenario_version_id_revision_id_organization_id_fkey");
        });

        modelBuilder.Entity<ReleasePackage>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("release_packages_pkey");

            entity.ToTable("release_packages");

            entity.HasIndex(e => e.ReleaseId, "release_packages_release_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.BuildTarget)
                .HasDefaultValueSql("'Android'::text")
                .HasColumnName("build_target");
            entity.Property(e => e.CandidateArtifactId).HasColumnName("candidate_artifact_id");
            entity.Property(e => e.ChecksumSha256)
                .HasMaxLength(64)
                .HasColumnName("checksum_sha256");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.ManifestSha256)
                .HasMaxLength(64)
                .HasColumnName("manifest_sha256");
            entity.Property(e => e.ManifestUrl).HasColumnName("manifest_url");
            entity.Property(e => e.MinRuntimeVersion).HasColumnName("min_runtime_version");
            entity.Property(e => e.PackageSizeBytes).HasColumnName("package_size_bytes");
            entity.Property(e => e.PackageUrl).HasColumnName("package_url");
            entity.Property(e => e.ReleaseId).HasColumnName("release_id");
            entity.Property(e => e.SchemaVersion)
                .HasDefaultValueSql("'1.0'::text")
                .HasColumnName("schema_version");

            entity.HasOne(d => d.CandidateArtifact).WithMany(p => p.ReleasePackages)
                .HasForeignKey(d => d.CandidateArtifactId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("release_packages_candidate_artifact_id_fkey");

            entity.HasOne(d => d.Release).WithOne(p => p.ReleasePackage)
                .HasForeignKey<ReleasePackage>(d => d.ReleaseId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("release_packages_release_id_fkey");
        });

        modelBuilder.Entity<ReleaseQrCode>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("release_qr_codes_pkey");

            entity.ToTable("release_qr_codes");

            entity.HasIndex(e => e.ReleaseId, "qr_release");

            entity.HasIndex(e => e.TrainingId, "qr_training");

            entity.HasIndex(e => new { e.Id, e.TrainingId, e.ReleaseId, e.OrganizationId }, "release_qr_codes_id_training_id_release_id_organization_id_key").IsUnique();

            entity.HasIndex(e => e.QrHash, "release_qr_codes_qr_hash_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.Label).HasColumnName("label");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.QrHash)
                .HasMaxLength(64)
                .HasComment("SHA-256 of a high-entropy opaque token. Original token returned once for printing; rotation creates a new row.")
                .HasColumnName("qr_hash");
            entity.Property(e => e.ReleaseId).HasColumnName("release_id");
            entity.Property(e => e.RevisionFloorId)
                .HasComment("Placement metadata only; does not override the immutable scenario spawn.")
                .HasColumnName("revision_floor_id");
            entity.Property(e => e.TrainingId).HasColumnName("training_id");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.ReleaseQrCodes)
                .HasForeignKey(d => d.CreatedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("release_qr_codes_created_by_fkey");

            entity.HasOne(d => d.RevisionFloor).WithMany(p => p.ReleaseQrCodes)
                .HasForeignKey(d => d.RevisionFloorId)
                .HasConstraintName("release_qr_codes_revision_floor_id_fkey");

            entity.HasOne(d => d.Training).WithMany(p => p.ReleaseQrCodes)
                .HasPrincipalKey(p => new { p.Id, p.ReleaseId, p.OrganizationId })
                .HasForeignKey(d => new { d.TrainingId, d.ReleaseId, d.OrganizationId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("release_qr_codes_training_id_release_id_organization_id_fkey");
        });

        modelBuilder.Entity<Revision>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("revisions_pkey");

            entity.ToTable("revisions");

            entity.HasIndex(e => new { e.BuildingId, e.VersionLabel }, "revisions_building_id_version_label_key").IsUnique();

            entity.HasIndex(e => new { e.Id, e.BuildingId, e.OrganizationId }, "revisions_id_building_id_organization_id_key").IsUnique();

            entity.HasIndex(e => new { e.Id, e.OrganizationId }, "revisions_id_organization_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.BuildingId).HasColumnName("building_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.UploadedBy).HasColumnName("uploaded_by");
            entity.Property(e => e.VersionLabel).HasColumnName("version_label");

            entity.HasOne(d => d.UploadedByNavigation).WithMany(p => p.Revisions)
                .HasForeignKey(d => d.UploadedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("revisions_uploaded_by_fkey");

            entity.HasOne(d => d.Building).WithMany(p => p.Revisions)
                .HasPrincipalKey(p => new { p.Id, p.OrganizationId })
                .HasForeignKey(d => new { d.BuildingId, d.OrganizationId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("revisions_building_id_organization_id_fkey");
        });

        modelBuilder.Entity<RevisionArtifact>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("revision_artifacts_pkey");

            entity.ToTable("revision_artifacts");

            entity.HasIndex(e => e.RevisionId, "artifacts_revision");

            entity.HasIndex(e => new { e.Id, e.RevisionId }, "revision_artifacts_id_revision_id_key").IsUnique();

            entity.HasIndex(e => new { e.JobId, e.ArtifactType, e.Sha256Hash }, "revision_artifacts_job_id_artifact_type_sha256_hash_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.ArtifactType).HasColumnName("artifact_type");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.JobId).HasColumnName("job_id");
            entity.Property(e => e.Metadata)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("metadata");
            entity.Property(e => e.ObjectKey).HasColumnName("object_key");
            entity.Property(e => e.RevisionId).HasColumnName("revision_id");
            entity.Property(e => e.SchemaVersion).HasColumnName("schema_version");
            entity.Property(e => e.Sha256Hash)
                .HasMaxLength(64)
                .HasColumnName("sha256_hash");
            entity.Property(e => e.SizeBytes).HasColumnName("size_bytes");

            entity.HasOne(d => d.Revision).WithMany(p => p.RevisionArtifacts)
                .HasForeignKey(d => d.RevisionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("revision_artifacts_revision_id_fkey");

            entity.HasOne(d => d.ProcessingJob).WithMany(p => p.RevisionArtifacts)
                .HasPrincipalKey(p => new { p.Id, p.RevisionId })
                .HasForeignKey(d => new { d.JobId, d.RevisionId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("revision_artifacts_job_id_revision_id_fkey");
        });

        modelBuilder.Entity<RevisionFloor>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("revision_floors_pkey");

            entity.ToTable("revision_floors", tb => tb.HasComment("Immutable floor snapshot used by content, QR labels and historical replay; changes require a new revision."));

            entity.HasIndex(e => new { e.Id, e.RevisionId }, "revision_floors_id_revision_id_key").IsUnique();

            entity.HasIndex(e => new { e.RevisionId, e.BuildingFloorId }, "revision_floors_revision_id_building_floor_id_key").IsUnique();

            entity.HasIndex(e => new { e.RevisionId, e.IfcGuid }, "revision_floors_revision_id_ifc_guid_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.BuildingFloorId).HasColumnName("building_floor_id");
            entity.Property(e => e.BuildingId).HasColumnName("building_id");
            entity.Property(e => e.CoordinateTransform)
                .HasColumnType("jsonb")
                .HasColumnName("coordinate_transform");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.ElevationMeters)
                .HasPrecision(10, 3)
                .HasColumnName("elevation_meters");
            entity.Property(e => e.FloorName).HasColumnName("floor_name");
            entity.Property(e => e.FloorNumber).HasColumnName("floor_number");
            entity.Property(e => e.IfcGuid).HasColumnName("ifc_guid");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.RevisionId).HasColumnName("revision_id");

            entity.HasOne(d => d.BuildingFloor).WithMany(p => p.RevisionFloors)
                .HasPrincipalKey(p => new { p.Id, p.BuildingId })
                .HasForeignKey(d => new { d.BuildingFloorId, d.BuildingId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("revision_floors_building_floor_id_building_id_fkey");

            entity.HasOne(d => d.Revision).WithMany(p => p.RevisionFloors)
                .HasPrincipalKey(p => new { p.Id, p.BuildingId, p.OrganizationId })
                .HasForeignKey(d => new { d.RevisionId, d.BuildingId, d.OrganizationId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("revision_floors_revision_id_building_id_organization_id_fkey");
        });

        modelBuilder.Entity<RevisionIssue>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("revision_issues_pkey");

            entity.ToTable("revision_issues");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.Code).HasColumnName("code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.Details)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("details");
            entity.Property(e => e.IfcGuid).HasColumnName("ifc_guid");
            entity.Property(e => e.Message).HasColumnName("message");
            entity.Property(e => e.Severity).HasColumnName("severity");
            entity.Property(e => e.ValidationRunId).HasColumnName("validation_run_id");

            entity.HasOne(d => d.ValidationRun).WithMany(p => p.RevisionIssues)
                .HasForeignKey(d => d.ValidationRunId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("revision_issues_validation_run_id_fkey");
        });

        modelBuilder.Entity<RevisionProcessingLog>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("revision_processing_logs_pkey");

            entity.ToTable("revision_processing_logs");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            
            entity.Property(e => e.DurationMs).HasColumnName("duration_ms");
            entity.Property(e => e.JobId).HasColumnName("job_id");
            entity.Property(e => e.LoggedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("logged_at");
            entity.Property(e => e.Message).HasColumnName("message");
            entity.Property(e => e.RevisionId).HasColumnName("revision_id");

            entity.HasOne(d => d.Revision).WithMany(p => p.RevisionProcessingLogs)
                .HasForeignKey(d => d.RevisionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("revision_processing_logs_revision_id_fkey");

            entity.HasOne(d => d.ProcessingJob).WithMany(p => p.RevisionProcessingLogs)
                .HasPrincipalKey(p => new { p.Id, p.RevisionId })
                .HasForeignKey(d => new { d.JobId, d.RevisionId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("revision_processing_logs_job_id_revision_id_fkey");
        });

        modelBuilder.Entity<RevisionReview>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("revision_reviews_pkey");

            entity.ToTable("revision_reviews");

            entity.HasIndex(e => e.ScenarioVersionId, "one_scenario_confirmation")
                .IsUnique()
                .HasFilter("(action = 'ConfirmForTraining'::review_action_enum)");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AnnotationSetId).HasColumnName("annotation_set_id");
            entity.Property(e => e.ReviewMessage).HasColumnName("review_message");
            entity.Property(e => e.ReviewedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("reviewed_at");
            entity.Property(e => e.ReviewedBy).HasColumnName("reviewed_by");
            entity.Property(e => e.RevisionId).HasColumnName("revision_id");
            entity.Property(e => e.ScenarioVersionId).HasColumnName("scenario_version_id");
            entity.Property(e => e.ValidationRunId).HasColumnName("validation_run_id");

            entity.HasOne(d => d.ReviewedByNavigation).WithMany(p => p.RevisionReviews)
                .HasForeignKey(d => d.ReviewedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("revision_reviews_reviewed_by_fkey");

            entity.HasOne(d => d.Revision).WithMany(p => p.RevisionReviews)
                .HasForeignKey(d => d.RevisionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("revision_reviews_revision_id_fkey");

            entity.HasOne(d => d.ScenarioVersion).WithOne(p => p.RevisionReview)
                .HasForeignKey<RevisionReview>(d => d.ScenarioVersionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("revision_reviews_scenario_version_id_fkey");

            entity.HasOne(d => d.ValidationRun).WithMany(p => p.RevisionReviews)
                .HasForeignKey(d => d.ValidationRunId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("revision_reviews_validation_run_id_fkey");

            entity.HasOne(d => d.AnnotationSet).WithMany(p => p.RevisionReviews)
                .HasPrincipalKey(p => new { p.Id, p.RevisionId })
                .HasForeignKey(d => new { d.AnnotationSetId, d.RevisionId })
                .HasConstraintName("revision_reviews_annotation_set_id_revision_id_fkey");
        });

        modelBuilder.Entity<Scenario>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("scenarios_pkey");

            entity.ToTable("scenarios", tb => tb.HasComment("Logical training scenario belonging to a building; versions may pin different compatible building revisions."));

            entity.HasIndex(e => new { e.Id, e.BuildingId, e.OrganizationId }, "scenarios_id_building_id_organization_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.BuildingId).HasColumnName("building_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.Scenarios)
                .HasForeignKey(d => d.CreatedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("scenarios_created_by_fkey");

            entity.HasOne(d => d.Building).WithMany(p => p.Scenarios)
                .HasPrincipalKey(p => new { p.Id, p.OrganizationId })
                .HasForeignKey(d => new { d.BuildingId, d.OrganizationId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("scenarios_building_id_organization_id_fkey");
        });

        modelBuilder.Entity<ScenarioVersion>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("scenario_versions_pkey");

            entity.ToTable("scenario_versions", tb => tb.HasComment("Append-only snapshot. Confirmation is per version through revision_reviews, not an exclusive lock on all scenarios of the revision."));

            entity.HasIndex(e => new { e.Id, e.RevisionId, e.OrganizationId }, "scenario_versions_id_revision_id_organization_id_key").IsUnique();

            entity.HasIndex(e => new { e.ScenarioId, e.VersionNumber }, "scenario_versions_scenario_id_version_number_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AlgorithmVersion).HasColumnName("algorithm_version");
            entity.Property(e => e.BlockedElements)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("blocked_elements");
            entity.Property(e => e.BuildingId).HasColumnName("building_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.FireSourceConfig)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("fire_source_config");
            entity.Property(e => e.GoalConfig)
                .HasColumnType("jsonb")
                .HasColumnName("goal_config");
            entity.Property(e => e.ModePolicy)
                .HasColumnType("jsonb")
                .HasColumnName("mode_policy");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.NpcConfig)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("npc_config");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.RandomSeed).HasColumnName("random_seed");
            entity.Property(e => e.ReplanIntervalSeconds)
                .HasDefaultValue(5)
                .HasColumnName("replan_interval_seconds");
            entity.Property(e => e.RevisionId).HasColumnName("revision_id");
            entity.Property(e => e.RoutingConfig)
                .HasColumnType("jsonb")
                .HasColumnName("routing_config");
            entity.Property(e => e.SafetyThresholds)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("safety_thresholds");
            entity.Property(e => e.ScenarioHash)
                .HasMaxLength(64)
                .HasColumnName("scenario_hash");
            entity.Property(e => e.ScenarioId).HasColumnName("scenario_id");
            entity.Property(e => e.SchemaVersion)
                .HasDefaultValueSql("'1.0'::text")
                .HasColumnName("schema_version");
            entity.Property(e => e.ScoringConfig)
                .HasColumnType("jsonb")
                .HasColumnName("scoring_config");
            entity.Property(e => e.SpawnConfig)
                .HasColumnType("jsonb")
                .HasColumnName("spawn_config");
            entity.Property(e => e.TimeLimitSeconds).HasColumnName("time_limit_seconds");
            entity.Property(e => e.VersionNumber).HasColumnName("version_number");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.ScenarioVersions)
                .HasForeignKey(d => d.CreatedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("scenario_versions_created_by_fkey");

            entity.HasOne(d => d.Revision).WithMany(p => p.ScenarioVersions)
                .HasPrincipalKey(p => new { p.Id, p.BuildingId, p.OrganizationId })
                .HasForeignKey(d => new { d.RevisionId, d.BuildingId, d.OrganizationId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("scenario_versions_revision_id_building_id_organization_id_fkey");

            entity.HasOne(d => d.Scenario).WithMany(p => p.ScenarioVersions)
                .HasPrincipalKey(p => new { p.Id, p.BuildingId, p.OrganizationId })
                .HasForeignKey(d => new { d.ScenarioId, d.BuildingId, d.OrganizationId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("scenario_versions_scenario_id_building_id_organization_id_fkey");
        });

        modelBuilder.Entity<ServicePackage>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("service_packages_pkey");

            entity.ToTable("service_packages");

            entity.HasIndex(e => e.Code, "idx_service_packages_active").HasFilter("is_active");

            entity.HasIndex(e => e.CreatedBy, "idx_service_packages_created_by");

            entity.HasIndex(e => e.Code, "service_packages_code_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.Code)
                .HasMaxLength(50)
                .HasColumnName("code");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Currency)
                .HasMaxLength(3)
                .HasDefaultValueSql("'VND'::character varying")
                .HasColumnName("currency");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.DurationMonths).HasColumnName("duration_months");
            entity.Property(e => e.Features)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("features");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.Name)
                .HasMaxLength(255)
                .HasColumnName("name");
            entity.Property(e => e.UnitPrice)
                .HasPrecision(14, 2)
                .HasColumnName("unit_price");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.ServicePackages)
                .HasForeignKey(d => d.CreatedBy)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("service_packages_created_by_fkey");
        });

        modelBuilder.Entity<Session>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("sessions_pkey");

            entity.ToTable("sessions");

            entity.HasIndex(e => new { e.OrganizationId, e.StartedAt }, "sessions_org_history").IsDescending(false, true);

            entity.HasIndex(e => new { e.TraineeUserId, e.StartedAt }, "sessions_personal_history").IsDescending(false, true);

            entity.HasIndex(e => e.ReleaseId, "sessions_release");

            entity.HasIndex(e => new { e.TraineeUserId, e.StartKey }, "sessions_trainee_user_id_start_key_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AppVersion).HasColumnName("app_version");
            entity.Property(e => e.DeviceId).HasColumnName("device_id");
            entity.Property(e => e.EndedAt).HasColumnName("ended_at");
            entity.Property(e => e.LaunchedAt).HasColumnName("launched_at");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.ProtocolVersion)
                .HasDefaultValueSql("'1.0'::text")
                .HasColumnName("protocol_version");
            entity.Property(e => e.QrCodeId).HasColumnName("qr_code_id");
            entity.Property(e => e.ReleaseHash)
                .HasMaxLength(64)
                .HasColumnName("release_hash");
            entity.Property(e => e.ReleaseId).HasColumnName("release_id");
            entity.Property(e => e.ScenarioHash)
                .HasMaxLength(64)
                .HasColumnName("scenario_hash");
            entity.Property(e => e.ScenarioVersionId).HasColumnName("scenario_version_id");
            entity.Property(e => e.StartKey).HasColumnName("start_key");
            entity.Property(e => e.StartedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("started_at");
            entity.Property(e => e.TerminalReason).HasColumnName("terminal_reason");
            entity.Property(e => e.TraineeUserId).HasColumnName("trainee_user_id");
            entity.Property(e => e.TrainingId).HasColumnName("training_id");
            entity.Property(e => e.UnityVersion).HasColumnName("unity_version");

            entity.HasOne(d => d.TraineeUser).WithMany(p => p.Sessions)
                .HasForeignKey(d => d.TraineeUserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("sessions_trainee_user_id_fkey");

            entity.HasOne(d => d.UserDevice).WithMany(p => p.Sessions)
                .HasPrincipalKey(p => new { p.Id, p.UserId })
                .HasForeignKey(d => new { d.DeviceId, d.TraineeUserId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("sessions_device_id_trainee_user_id_fkey");

            entity.HasOne(d => d.ReleaseQrCode).WithMany(p => p.Sessions)
                .HasPrincipalKey(p => new { p.Id, p.TrainingId, p.ReleaseId, p.OrganizationId })
                .HasForeignKey(d => new { d.QrCodeId, d.TrainingId, d.ReleaseId, d.OrganizationId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("sessions_qr_code_id_training_id_release_id_organization_id_fkey");

            entity.HasOne(d => d.Training).WithMany(p => p.Sessions)
                .HasPrincipalKey(p => new { p.Id, p.ReleaseId, p.ScenarioVersionId, p.OrganizationId })
                .HasForeignKey(d => new { d.TrainingId, d.ReleaseId, d.ScenarioVersionId, d.OrganizationId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("sessions_training_id_release_id_scenario_version_id_organi_fkey");
        });

        modelBuilder.Entity<SessionCheckpoint>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("session_checkpoints_pkey");

            entity.ToTable("session_checkpoints");

            entity.HasIndex(e => new { e.SessionId, e.SequenceNumber }, "session_checkpoints_session_id_sequence_number_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.ActiveObjectives)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("active_objectives");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.HazardTimeStep).HasColumnName("hazard_time_step");
            entity.Property(e => e.NpcStates)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("npc_states");
            entity.Property(e => e.PlayerStatus)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("player_status");
            entity.Property(e => e.PlayerTransform)
                .HasColumnType("jsonb")
                .HasColumnName("player_transform");
            entity.Property(e => e.ReleaseHash)
                .HasMaxLength(64)
                .HasColumnName("release_hash");
            entity.Property(e => e.ScenarioHash)
                .HasMaxLength(64)
                .HasColumnName("scenario_hash");
            entity.Property(e => e.SequenceNumber).HasColumnName("sequence_number");
            entity.Property(e => e.SessionId).HasColumnName("session_id");
            entity.Property(e => e.WorldInteractiveStates)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("world_interactive_states");

            entity.HasOne(d => d.Session).WithMany(p => p.SessionCheckpoints)
                .HasForeignKey(d => d.SessionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("session_checkpoints_session_id_fkey");
        });

        modelBuilder.Entity<SessionEvent>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("session_events_pkey");

            entity.ToTable("session_events");

            entity.HasIndex(e => new { e.SessionId, e.ClientEventId }, "session_events_session_id_client_event_id_key").IsUnique();

            entity.HasIndex(e => new { e.SessionId, e.SequenceNumber }, "session_events_session_id_sequence_number_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.ClientEventId).HasColumnName("client_event_id");
            entity.Property(e => e.ElapsedMs).HasColumnName("elapsed_ms");
            entity.Property(e => e.EventData)
                .HasDefaultValueSql("'{}'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("event_data");
            entity.Property(e => e.EventType).HasColumnName("event_type");
            entity.Property(e => e.ReceivedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("received_at");
            entity.Property(e => e.RecordedAt).HasColumnName("recorded_at");
            entity.Property(e => e.SchemaVersion)
                .HasDefaultValueSql("'1.0'::text")
                .HasColumnName("schema_version");
            entity.Property(e => e.SequenceNumber).HasColumnName("sequence_number");
            entity.Property(e => e.SessionId).HasColumnName("session_id");

            entity.HasOne(d => d.Session).WithMany(p => p.SessionEvents)
                .HasForeignKey(d => d.SessionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("session_events_session_id_fkey");
        });

        modelBuilder.Entity<SessionResult>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("session_results_pkey");

            entity.ToTable("session_results", tb => tb.HasComment("One immutable server-accepted result per session. Backend validates/calculates rubric before insert; no client score trust implied."));

            entity.HasIndex(e => e.SessionId, "session_results_session_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.ClientEndedAt).HasColumnName("client_ended_at");
            entity.Property(e => e.ClientStartedAt).HasColumnName("client_started_at");
            entity.Property(e => e.CompletionKey).HasColumnName("completion_key");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.ExitPointId).HasColumnName("exit_point_id");
            entity.Property(e => e.HazardExposureScore)
                .HasPrecision(12, 2)
                .HasColumnName("hazard_exposure_score");
            entity.Property(e => e.LastEventSequence).HasColumnName("last_event_sequence");
            entity.Property(e => e.PathTraveled)
                .HasDefaultValueSql("'[]'::jsonb")
                .HasColumnType("jsonb")
                .HasColumnName("path_traveled");
            entity.Property(e => e.ReachedExit).HasColumnName("reached_exit");
            entity.Property(e => e.ResultSchemaVersion)
                .HasDefaultValueSql("'1.0'::text")
                .HasColumnName("result_schema_version");
            entity.Property(e => e.RubricVersion).HasColumnName("rubric_version");
            entity.Property(e => e.Score)
                .HasPrecision(5, 2)
                .HasColumnName("score");
            entity.Property(e => e.SessionId).HasColumnName("session_id");
            entity.Property(e => e.SubmissionPayload)
                .HasColumnType("jsonb")
                .HasColumnName("submission_payload");
            entity.Property(e => e.SyncedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("synced_at");
            entity.Property(e => e.TimeTakenSeconds).HasColumnName("time_taken_seconds");
            entity.Property(e => e.TotalDistanceMeters)
                .HasPrecision(12, 2)
                .HasColumnName("total_distance_meters");
            entity.Property(e => e.WrongExits).HasColumnName("wrong_exits");

            entity.HasOne(d => d.Session).WithOne(p => p.SessionResult)
                .HasForeignKey<SessionResult>(d => d.SessionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("session_results_session_id_fkey");
        });

        modelBuilder.Entity<SourceDocument>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("source_documents_pkey");

            entity.ToTable("source_documents", tb => tb.HasComment("Phase 1: one accepted source IFC per revision; storage_url is a private stable object key, never a presigned URL."));

            entity.HasIndex(e => new { e.Id, e.RevisionId }, "source_documents_id_revision_id_key").IsUnique();

            entity.HasIndex(e => e.RevisionId, "source_documents_revision_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.FileSizeBytes).HasColumnName("file_size_bytes");
            entity.Property(e => e.MimeType).HasColumnName("mime_type");
            entity.Property(e => e.OriginalFilename).HasColumnName("original_filename");
            entity.Property(e => e.QuarantineNote).HasColumnName("quarantine_note");
            entity.Property(e => e.RevisionId).HasColumnName("revision_id");
            entity.Property(e => e.Sha256Hash)
                .HasMaxLength(64)
                .HasColumnName("sha256_hash");
            entity.Property(e => e.SourceTool).HasColumnName("source_tool");
            entity.Property(e => e.StorageUrl).HasColumnName("storage_url");
            entity.Property(e => e.UploadedBy).HasColumnName("uploaded_by");
            entity.Property(e => e.UsageRights).HasColumnName("usage_rights");

            entity.HasOne(d => d.Revision).WithOne(p => p.SourceDocument)
                .HasForeignKey<SourceDocument>(d => d.RevisionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("source_documents_revision_id_fkey");

            entity.HasOne(d => d.UploadedByNavigation).WithMany(p => p.SourceDocuments)
                .HasForeignKey(d => d.UploadedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("source_documents_uploaded_by_fkey");
        });

        modelBuilder.Entity<SupportTicket>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("support_tickets_pkey");

            entity.ToTable("support_tickets");

            entity.HasIndex(e => e.AssignedTo, "idx_support_tickets_assigned_to");

            entity.HasIndex(e => e.CreatedBy, "idx_support_tickets_created_by");

            entity.HasIndex(e => e.FeedbackId, "idx_support_tickets_feedback");

            entity.HasIndex(e => e.OrganizationId, "idx_support_tickets_organization");

            entity.HasIndex(e => e.SessionId, "idx_support_tickets_session");

            entity.HasIndex(e => e.TicketNumber, "support_tickets_ticket_number_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AssignedTo).HasColumnName("assigned_to");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.FeedbackId).HasColumnName("feedback_id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.ResolvedAt).HasColumnName("resolved_at");
            entity.Property(e => e.SessionId).HasColumnName("session_id");
            entity.Property(e => e.Subject)
                .HasMaxLength(255)
                .HasColumnName("subject");
            entity.Property(e => e.TicketNumber)
                .HasMaxLength(50)
                .HasColumnName("ticket_number");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.AssignedToNavigation).WithMany(p => p.SupportTicketAssignedToNavigations)
                .HasForeignKey(d => d.AssignedTo)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("support_tickets_assigned_to_fkey");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.SupportTicketCreatedByNavigations)
                .HasForeignKey(d => d.CreatedBy)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("support_tickets_created_by_fkey");

            entity.HasOne(d => d.Feedback).WithMany(p => p.SupportTickets)
                .HasForeignKey(d => d.FeedbackId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("support_tickets_feedback_id_fkey");

            entity.HasOne(d => d.Organization).WithMany(p => p.SupportTickets)
                .HasForeignKey(d => d.OrganizationId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("support_tickets_organization_id_fkey");

            entity.HasOne(d => d.Session).WithMany(p => p.SupportTickets)
                .HasForeignKey(d => d.SessionId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("support_tickets_session_id_fkey");
        });

        modelBuilder.Entity<Training>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("trainings_pkey");

            entity.ToTable("trainings");

            entity.HasIndex(e => new { e.Id, e.ReleaseId, e.OrganizationId }, "trainings_id_release_id_organization_id_key").IsUnique();

            entity.HasIndex(e => new { e.Id, e.ReleaseId, e.ScenarioVersionId, e.OrganizationId }, "trainings_id_release_id_scenario_version_id_organization_id_key").IsUnique();

            entity.HasIndex(e => e.ReleaseId, "trainings_release");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AllowedModes)
                .HasDefaultValueSql("ARRAY['Learn'::text, 'Guided'::text, 'Assessment'::text]")
                .HasColumnName("allowed_modes");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.EndDate).HasColumnName("end_date");
            entity.Property(e => e.MaxAttempts)
                .HasComment("NULL = unlimited. Positive value limits Assessment session creation per Trainee and Training, including launch failures; Learn/Guided unlimited.")
                .HasColumnName("max_attempts");
            entity.Property(e => e.Name).HasColumnName("name");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.ReleaseId).HasColumnName("release_id");
            entity.Property(e => e.ScenarioVersionId).HasColumnName("scenario_version_id");
            entity.Property(e => e.StartDate).HasColumnName("start_date");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.Training)
                .HasForeignKey(d => d.CreatedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("trainings_created_by_fkey");

            entity.HasOne(d => d.Release).WithMany(p => p.Training)
                .HasPrincipalKey(p => new { p.Id, p.ScenarioVersionId, p.OrganizationId })
                .HasForeignKey(d => new { d.ReleaseId, d.ScenarioVersionId, d.OrganizationId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("trainings_release_id_scenario_version_id_organization_id_fkey");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("users_pkey");

            entity.ToTable("users");

            entity.HasIndex(e => e.Email, "users_email_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");
            entity.Property(e => e.Email).HasColumnName("email");
            entity.Property(e => e.FullName).HasColumnName("full_name");
            entity.Property(e => e.IsActive)
                .HasDefaultValue(true)
                .HasColumnName("is_active");
            entity.Property(e => e.LastLoginAt).HasColumnName("last_login_at");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.FirebaseUid)
                .HasMaxLength(128)
                .HasColumnName("firebase_uid");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Organization).WithMany(p => p.Users)
                .HasForeignKey(d => d.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("users_organization_id_fkey");
        });

        modelBuilder.Entity<UserDevice>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("user_devices_pkey");

            entity.ToTable("user_devices");

            entity.HasIndex(e => new { e.Id, e.UserId }, "user_devices_id_user_id_key").IsUnique();

            entity.HasIndex(e => new { e.UserId, e.DeviceUuid }, "user_devices_user_id_device_uuid_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AppVersion).HasColumnName("app_version");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DeviceModel).HasColumnName("device_model");
            entity.Property(e => e.DeviceUuid).HasColumnName("device_uuid");
            entity.Property(e => e.FcmToken)
                .HasMaxLength(255)
                .HasColumnName("fcm_token");
            entity.Property(e => e.LastSeenAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("last_seen_at");
            entity.Property(e => e.OsVersion).HasColumnName("os_version");
            entity.Property(e => e.UserId).HasColumnName("user_id");

            entity.HasOne(d => d.User).WithMany(p => p.UserDevices)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("user_devices_user_id_fkey");
        });

        modelBuilder.Entity<ValidationRun>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("validation_runs_pkey");

            entity.ToTable("validation_runs", tb => tb.HasComment("Append-only attestation from a trusted validator; SQL checks bindings, not geometry, routing or cryptographic file contents."));

            entity.HasIndex(e => new { e.RevisionId, e.Kind, e.Outcome }, "validation_revision");

            entity.HasIndex(e => e.JobId, "validation_runs_job_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AnnotationSetId).HasColumnName("annotation_set_id");
            entity.Property(e => e.CandidateArtifactId).HasColumnName("candidate_artifact_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.JobId).HasColumnName("job_id");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.Outcome).HasColumnName("outcome");
            entity.Property(e => e.Report)
                .HasColumnType("jsonb")
                .HasColumnName("report");
            entity.Property(e => e.RevisionId).HasColumnName("revision_id");
            entity.Property(e => e.ScenarioHash)
                .HasMaxLength(64)
                .HasColumnName("scenario_hash");
            entity.Property(e => e.ScenarioVersionId).HasColumnName("scenario_version_id");
            entity.Property(e => e.ValidatorVersion).HasColumnName("validator_version");

            entity.HasOne(d => d.Revision).WithMany(p => p.ValidationRuns)
                .HasForeignKey(d => d.RevisionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("validation_runs_revision_id_fkey");

            entity.HasOne(d => d.ScenarioVersion).WithMany(p => p.ValidationRuns)
                .HasForeignKey(d => d.ScenarioVersionId)
                .HasConstraintName("validation_runs_scenario_version_id_fkey");

            entity.HasOne(d => d.AnnotationSet).WithMany(p => p.ValidationRuns)
                .HasPrincipalKey(p => new { p.Id, p.RevisionId })
                .HasForeignKey(d => new { d.AnnotationSetId, d.RevisionId })
                .HasConstraintName("validation_runs_annotation_set_id_revision_id_fkey");

            entity.HasOne(d => d.RevisionArtifact).WithMany(p => p.ValidationRuns)
                .HasPrincipalKey(p => new { p.Id, p.RevisionId })
                .HasForeignKey(d => new { d.CandidateArtifactId, d.RevisionId })
                .HasConstraintName("validation_runs_candidate_artifact_id_revision_id_fkey");

            entity.HasOne(d => d.ProcessingJob).WithMany(p => p.ValidationRuns)
                .HasPrincipalKey(p => new { p.Id, p.RevisionId })
                .HasForeignKey(d => new { d.JobId, d.RevisionId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("validation_runs_job_id_revision_id_fkey");
        });

        modelBuilder.Entity<PasswordResetToken>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("password_reset_tokens_pkey");

            entity.ToTable("password_reset_tokens");

            entity.HasIndex(e => e.UserId).HasDatabaseName("ix_password_reset_tokens_user_id");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.UsedAt).HasColumnName("used_at");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");

            entity.HasOne(d => d.User)
                .WithMany()
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("password_reset_tokens_user_id_fkey");
        });

                modelBuilder.Entity<ScenarioDraft>(entity =>
        {
            entity.ToTable("scenario_drafts");
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.ScenarioId).HasColumnName("scenario_id");
            entity.Property(e => e.RevisionId).HasColumnName("revision_id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.BuildingId).HasColumnName("building_id");
            entity.Property(e => e.DraftNumber).HasColumnName("draft_number");
            entity.Property(e => e.State).HasColumnName("state").HasColumnType("jsonb");
            entity.Property(e => e.Source).HasColumnName("source");
            entity.Property(e => e.LastAiRequestId).HasColumnName("last_ai_request_id");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            entity.Property(e => e.Version).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
            
            entity.HasOne(d => d.Scenario).WithMany().HasForeignKey(d => d.ScenarioId);
            entity.HasOne(d => d.Revision).WithMany().HasForeignKey(d => d.RevisionId);
            entity.HasOne(d => d.Organization).WithMany().HasForeignKey(d => d.OrganizationId);
            entity.HasOne(d => d.Building).WithMany().HasForeignKey(d => d.BuildingId);
            entity.HasOne(d => d.CreatedByNavigation).WithMany().HasForeignKey(d => d.CreatedBy);
        });

        modelBuilder.Entity<PlaytestSession>(entity =>
        {
            entity.ToTable("playtest_sessions");
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.OrganizationId).HasColumnName("organization_id");
            entity.Property(e => e.BuildingId).HasColumnName("building_id");
            entity.Property(e => e.RevisionId).HasColumnName("revision_id");
            entity.Property(e => e.ScenarioDraftId).HasColumnName("scenario_draft_id");
            entity.Property(e => e.ScenarioVersionId).HasColumnName("scenario_version_id");
            entity.Property(e => e.ServiceEntitlementId).HasColumnName("service_entitlement_id");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.PackageHash).HasColumnName("package_hash");
            entity.Property(e => e.ProtocolVersion).HasColumnName("protocol_version");
            entity.Property(e => e.ManifestSchemaVersion).HasColumnName("manifest_schema_version");
            entity.Property(e => e.PrepareIdempotencyKey).HasColumnName("prepare_idempotency_key");
            entity.Property(e => e.RuntimeVersion).HasColumnName("runtime_version");
            entity.Property(e => e.StartIdempotencyKey).HasColumnName("start_idempotency_key");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.CompletionIdempotencyKey).HasColumnName("completion_idempotency_key");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.EndedAt).HasColumnName("ended_at");

            entity.HasOne(d => d.Organization).WithMany().HasForeignKey(d => d.OrganizationId);
            entity.HasOne(d => d.Building).WithMany().HasForeignKey(d => d.BuildingId);
            entity.HasOne(d => d.Revision).WithMany().HasForeignKey(d => d.RevisionId);
            entity.HasOne(d => d.ScenarioDraft).WithMany().HasForeignKey(d => d.ScenarioDraftId);
            entity.HasOne(d => d.ScenarioVersion).WithMany().HasForeignKey(d => d.ScenarioVersionId);
            entity.HasOne(d => d.CreatedByNavigation).WithMany().HasForeignKey(d => d.CreatedBy);
        });

        modelBuilder.Entity<RuntimeCompatibilityCatalog>(entity =>
        {
            entity.ToTable("runtime_compatibility_catalog");
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.RuntimeVersion).HasColumnName("runtime_version");
            entity.Property(e => e.ProtocolVersion).HasColumnName("protocol_version");
            entity.Property(e => e.ManifestSchemaVersion).HasColumnName("manifest_schema_version");
            entity.Property(e => e.Capabilities).HasColumnName("capabilities").HasColumnType("jsonb");
            entity.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}




