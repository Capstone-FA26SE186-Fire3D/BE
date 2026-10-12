using System.Text.Json.Nodes;
using Fire3D.Application.Ifc;
using Fire3D.Application.Ifc.Commands.InitiateUpload;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Fire3D.API.Extensions;

/// <summary>Aligns request/response schemas with what the handlers enforce when the C# shape alone cannot express it.</summary>
public sealed class ContractSchemaTransformer : IOpenApiSchemaTransformer
{
    // Values written by the processing gates (processing_jobs, processing_job_attempts, validation_runs, revision_issues).
    public static readonly string[] ProcessingJobStatuses = ["Queued", "Running", "Succeeded", "Failed", "Cancelled"];
    public static readonly string[] ProcessingAttemptStatuses = ["Running", "Succeeded", "Failed", "Expired"];
    public static readonly string[] ValidationOutcomes = ["Passed", "Failed"];
    public static readonly string[] IssueSeverities = ["Info", "Warning", "Error", "Critical"];
    public static readonly string[] IssueStatuses = ["Open"];

    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        var type = context.JsonTypeInfo.Type;
        if (type == typeof(InitiateIfcUploadRequest) && schema.Properties is not null)
        {
            // The property stays nullable in C# so a missing hash reaches the handler's VALIDATION_ERROR (errors.sha256Hash).
            schema.Required ??= new HashSet<string>();
            schema.Required.Add("sha256Hash");
            schema.Properties["sha256Hash"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String, Pattern = "^[0-9a-fA-F]{64}$", MinLength = 64, MaxLength = 64,
                Description = "Required SHA-256 of the IFC bytes (64 hex characters). Missing or malformed returns 400 VALIDATION_ERROR with errors.sha256Hash."
            };
        }
        else if (type == typeof(ProcessingJobResponse))
            Enum(schema, "status", ProcessingJobStatuses, "ProcessingJobStatus. Succeeded means the worker finished; QA passing is the validation run outcome.");
        else if (type == typeof(ProcessingAttemptResponse))
            Enum(schema, "status", ProcessingAttemptStatuses, "ProcessingAttemptStatus. Expired is a lease that ran out; the job may be retried.");
        else if (type == typeof(ValidationRunResponse))
        {
            // Every stored run has an outcome; the C# default only keeps older cached shapes deserializable.
            Enum(schema, "outcome", ValidationOutcomes, "ValidationRunOutcome written by the gate.", required: true);
            Enum(schema, "status", ValidationOutcomes, "Same value as outcome (kept for compatibility).");
        }
        else if (type == typeof(RevisionIssueResponse))
        {
            Enum(schema, "severity", IssueSeverities, "IssueSeverity. Error and Critical block confirm-for-training and publish.");
            Enum(schema, "status", IssueStatuses, "IssueStatus.");
        }
        else if (type == typeof(RetryProcessingJobRequest) && schema.Properties is not null)
        {
            schema.Required = new HashSet<string> { "reason" };
            if (schema.Properties.TryGetValue("requestId", out var id) && id is OpenApiSchema requestId)
                requestId.Description = "Retry key (UUID). Prefer the Idempotency-Key header like other commands; this body field stays for compatibility and must match the header when both are sent.";
        }
        return Task.CompletedTask;
    }

    private static void Enum(OpenApiSchema schema, string property, string[] values, string description, bool required = false)
    {
        if (schema.Properties?.TryGetValue(property, out var found) != true || found is not OpenApiSchema target) return;
        var nullable = !required && target.Type.GetValueOrDefault().HasFlag(JsonSchemaType.Null);
        if (required) (schema.Required ??= new HashSet<string>()).Add(property);
        target.Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
        target.Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
        if (nullable) target.Enum.Add(null!);
        target.Description = description;
    }
}
