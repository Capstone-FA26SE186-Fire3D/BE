using Fire3D.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Persistence;

public partial class Fire3DDbContext
{
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new Configurations.RefreshTokenConfiguration());
        modelBuilder.Entity<User>().Property(x => x.PasswordHash).HasColumnName("password_hash");
        modelBuilder.Entity<User>().Property(x => x.AvatarStorageKey).HasColumnName("avatar_storage_key");
        modelBuilder.Entity<User>().Property(x => x.ProfileRevision).HasColumnName("profile_revision").HasDefaultValue(1L);
        modelBuilder.Entity<User>()
            .Property(x => x.Role)
            .HasColumnName("role")
            .HasColumnType("user_role_enum");

        modelBuilder.Entity<AvatarUploadIntent>(entity =>
        {
            entity.ToTable("avatar_upload_intents");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.ExpiresAt });
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.StagingObjectKey).HasColumnName("staging_object_key");
            entity.Property(x => x.ContentType).HasColumnName("content_type");
            entity.Property(x => x.ExpectedSizeBytes).HasColumnName("expected_size_bytes");
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Revision>(entity =>
        {
            entity.Property(x => x.PrimaryType)
                .HasColumnName("primary_type")
                .HasColumnType("file_type_enum");

            entity.Property(x => x.Status)
                .HasColumnName("status")
                .HasColumnType("revision_status_enum");
        });

        modelBuilder.Entity<SourceDocument>(entity =>
        {
            entity.Property(x => x.FileType)
                .HasColumnName("file_type")
                .HasColumnType("file_type_enum");

            entity.Property(x => x.QuarantineStatus)
                .HasColumnName("quarantine_status")
                .HasColumnType("quarantine_status_enum");
        });

        modelBuilder.Entity<RevisionProcessingLog>(entity =>
        {
            entity.Property(x => x.Step)
                .HasColumnName("step")
                .HasColumnType("processing_step_enum");

            entity.Property(x => x.Status)
                .HasColumnName("status")
                .HasColumnType("processing_step_status_enum");
        });

        modelBuilder.Entity<RevisionReview>()
            .Property(x => x.Action)
            .HasColumnName("action")
            .HasColumnType("review_action_enum");

        modelBuilder.Entity<Release>()
            .Property(x => x.Status)
            .HasColumnName("status")
            .HasColumnType("release_status_enum");

        modelBuilder.Entity<Training>(entity =>
        {
            entity.Property(x => x.Status)
                .HasColumnName("status")
                .HasColumnType("training_status_enum");

            entity.Property(x => x.Mode)
                .HasColumnName("mode")
                .HasColumnType("session_mode_enum");
        });

        modelBuilder.Entity<Session>(entity =>
        {
            entity.Property(x => x.Mode)
                .HasColumnName("mode")
                .HasColumnType("session_mode_enum");

            entity.Property(x => x.Status)
                .HasColumnName("status")
                .HasColumnType("session_status_enum");

            entity.Property(x => x.ContentStatusAtCompletion)
                .HasColumnName("content_status_at_completion")
                .HasColumnType("release_status_enum");
        });

        modelBuilder.Entity<AuditLog>()
            .Property(x => x.Action)
            .HasColumnName("action")
            .HasColumnType("audit_action_enum");

        modelBuilder.Entity<Quotation>()
            .Property(x => x.Status)
            .HasColumnName("status")
            .HasColumnType("quotation_status_enum");

        modelBuilder.Entity<PayosPaymentRequest>()
            .Property(x => x.Status)
            .HasColumnName("status")
            .HasColumnType("payment_request_status_enum");

        modelBuilder.Entity<PaymentTransaction>()
            .Property(x => x.Status)
            .HasColumnName("status")
            .HasColumnType("payment_transaction_status_enum");

        modelBuilder.Entity<Feedback>()
            .Property(x => x.Status)
            .HasColumnName("status")
            .HasColumnType("feedback_status_enum");

        modelBuilder.Entity<SupportTicket>(entity =>
        {
            entity.Property(x => x.Priority)
                .HasColumnName("priority")
                .HasColumnType("support_priority_enum");

            entity.Property(x => x.Status)
                .HasColumnName("status")
                .HasColumnType("support_ticket_status_enum");
        });
    }
}
