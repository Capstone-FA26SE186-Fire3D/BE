using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.NameTranslation;

namespace Fire3D.API.Extensions;

public static class DependencyInjection
{
    private static readonly NpgsqlNullNameTranslator EnumNames = new();

    public static IServiceCollection AddDatabase(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionName = configuration["Database:ConnectionName"] ?? "DefaultConnection";
        var connectionString = configuration.GetConnectionString(connectionName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Missing connection string: {connectionName}");
        }

        services.AddDbContext<Fire3DDbContext>(options =>
            options.UseNpgsql(connectionString, postgres =>
            {
                // Giữ nguyên chữ hoa/thường của các nhãn enum trong SQL.
                var names = EnumNames;

                postgres.MapEnum<UserRole>(
                    "user_role_enum", nameTranslator: names);

                postgres.MapEnum<FileType>(
                    "file_type_enum", nameTranslator: names);

                postgres.MapEnum<RevisionStatus>(
                    "revision_status_enum", nameTranslator: names);

                postgres.MapEnum<ReviewAction>(
                    "review_action_enum", nameTranslator: names);

                postgres.MapEnum<ReleaseStatus>(
                    "release_status_enum", nameTranslator: names);

                postgres.MapEnum<TrainingStatus>(
                    "training_status_enum", nameTranslator: names);

                postgres.MapEnum<QuarantineStatus>(
                    "quarantine_status_enum", nameTranslator: names);

                postgres.MapEnum<SessionMode>(
                    "session_mode_enum", nameTranslator: names);

                postgres.MapEnum<SessionStatus>(
                    "session_status_enum", nameTranslator: names);

                postgres.MapEnum<AuditAction>(
                    "audit_action_enum", nameTranslator: names);

                postgres.MapEnum<ProcessingStep>(
                    "processing_step_enum", nameTranslator: names);

                postgres.MapEnum<ProcessingStepStatus>(
                    "processing_step_status_enum", nameTranslator: names);

                postgres.MapEnum<QuotationStatus>(
                    "quotation_status_enum", nameTranslator: names);

                postgres.MapEnum<PaymentRequestStatus>(
                    "payment_request_status_enum", nameTranslator: names);

                postgres.MapEnum<PaymentTransactionStatus>(
                    "payment_transaction_status_enum", nameTranslator: names);

                postgres.MapEnum<FeedbackStatus>(
                    "feedback_status_enum", nameTranslator: names);

                postgres.MapEnum<SupportTicketStatus>(
                    "support_ticket_status_enum", nameTranslator: names);

                postgres.MapEnum<SupportPriority>(
                    "support_priority_enum", nameTranslator: names);
            }));

        return services;
    }
}
