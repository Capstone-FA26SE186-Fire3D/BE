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
    public static readonly string[] IfcQaCodes = ["IFC_NO_BODY_GEOMETRY", "IFC_BODY_NONSTANDARD_CONTEXT", "IFC_NO_SPACES", "IFC_SPACE_UNNAMED", "IFC_EXIT_UNKNOWN",
        "IFC_NO_FIRE_EQUIPMENT", "IFC_NO_WATER_SOURCE", "IFC_LOW_CONFIDENCE_DEVICE", "IFC_UNMAPPED_MATERIAL", "IFC_EXCESSIVE_VOIDS", "IFC_NOT_ARCHITECTURAL",
        "IFC_NO_FLOOR_UNDER_SPACE", "IFC_TRIANGLE_BUDGET_EXCEEDED", "IFC_DEFAULT_MASS_PRESENT", "IFC_UNSUPPORTED_FORMAT"];
    public static readonly string[] HistoryStatuses = ["Launching", "Running", "AwaitingSync", "Completed"];
    public static readonly string[] SessionStatuses = ["Prepared", "Expired", "Launching", "Running", "AwaitingSync", "Completed"];
    public static readonly string[] LearnerModes = ["Learn", "Guided", "Assessment"];
    public static readonly string[] ResultOutcomes = ["Passed", "NotPassed", "Incomplete", "NotAssessed"];
    public static readonly string[] EndReasons = ["Finished", "TimedOut", "Abandoned"];
    public static readonly string[] ResultReasons = ["MODE_NOT_ASSESSED", "ENDED_BEFORE_COMPLETION", "RUBRIC_NOT_SCORABLE", "MANDATORY_CRITERION_FAILED", "BELOW_PASS_THRESHOLD", "ALL_REQUIREMENTS_MET"];
    public static readonly string[] CriterionOperators = ["gte", "lte", "eq"];
    public static readonly string[] CriterionReasons = ["METRIC_UNAVAILABLE"];

    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken ct)
    {
        // Web JSON defaults also accept quoted numbers on input, which makes every numeric schema "integer|string" with a
        // pattern. Responses always write JSON numbers, so the contract publishes plain integer/number types (#83).
        if (schema.Type is { } kind && kind.HasFlag(JsonSchemaType.String) && (kind.HasFlag(JsonSchemaType.Integer) || kind.HasFlag(JsonSchemaType.Number)))
        {
            schema.Type = kind & ~JsonSchemaType.String;
            schema.Pattern = null;
        }
        var type = context.JsonTypeInfo.Type;
        if (type == typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))
        {
            // Every error response is ProblemDetails plus a stable code; validation adds field errors or JSONPath issues.
            schema.Properties ??= new Dictionary<string, IOpenApiSchema>();
            schema.Properties["code"] = new OpenApiSchema { Type = JsonSchemaType.String,
                Description = "Stable machine-readable error code, e.g. VALIDATION_ERROR, NOT_FOUND, EDITOR_SCHEMA_INVALID. Clients branch on this, never on title." };
            schema.Properties["errors"] = new OpenApiSchema { Type = JsonSchemaType.Object | JsonSchemaType.Null,
                AdditionalProperties = new OpenApiSchema { Type = JsonSchemaType.Array, Items = new OpenApiSchema { Type = JsonSchemaType.String } },
                Description = "Field errors for 400 VALIDATION_ERROR: field name → messages." };
            schema.Properties["issues"] = new OpenApiSchema { Type = JsonSchemaType.Array | JsonSchemaType.Null,
                Items = new OpenApiSchema { Type = JsonSchemaType.Object, Required = new HashSet<string> { "code", "path", "message" },
                    Properties = new Dictionary<string, IOpenApiSchema>
                    {
                        ["code"] = new OpenApiSchema { Type = JsonSchemaType.String },
                        ["path"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "JSONPath of the offending value, e.g. $.objects[0].placement." },
                        ["message"] = new OpenApiSchema { Type = JsonSchemaType.String }
                    } },
                Description = "Content issues for 422 EDITOR_SCHEMA_INVALID / CONTENT_INVALID." };
            schema.Properties["traceId"] = new OpenApiSchema { Type = JsonSchemaType.String };
            return Task.CompletedTask;
        }
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
            if (schema.Properties?.TryGetValue("issueCode", out var code) == true && code is OpenApiSchema issueCode)
                issueCode.Description = "Worker QA code; FE maps it to the user guidance (docs/ifc-qa-codes.md). IFC worker codes (Docs ifc_ingestion_visual_interaction.md 5.9): "
                    + string.Join(", ", IfcQaCodes) + ". Other codes may appear; show them generically.";
            Enum(schema, "status", IssueStatuses, "IssueStatus.");
        }
        // Learner sessions (#96): values written or derived by the v7 learner gates.
        else if (type == typeof(Fire3D.Application.Learning.TrainingHistoryItem))
        {
            Enum(schema, "status", HistoryStatuses, "Session state; completion is not passing. AwaitingSync = completion requested, events still missing.");
            Enum(schema, "mode", LearnerModes, "TrainingMode.");
            Enum(schema, "resultStatus", ResultOutcomes, "Stored outcome; null until the server stored a result.");
            Enum(schema, "endReason", EndReasons, "Completion end reason; null until completion is requested.");
        }
        else if (type == typeof(Fire3D.Application.Learning.TrainingSessionView))
        {
            Enum(schema, "status", SessionStatuses, "Prepared and Expired are preparations (no seat, not a play); the rest are started sessions.");
            Enum(schema, "mode", LearnerModes, "TrainingMode.");
        }
        else if (type == typeof(Fire3D.Application.Learning.SessionCompletionView))
            Enum(schema, "endReason", EndReasons, "Completion end reason.");
        else if (type == typeof(Fire3D.Application.Learning.TrainingResultView))
        {
            Enum(schema, "mode", LearnerModes, "TrainingMode. Only Assessment is scored.");
            Enum(schema, "outcome", ResultOutcomes, "Passed/NotPassed/Incomplete for Assessment, NotAssessed for Learn and Guided.");
            Enum(schema, "reason", ResultReasons, "MODE_NOT_ASSESSED: Learn/Guided · ENDED_BEFORE_COMPLETION: abandoned · RUBRIC_NOT_SCORABLE: no rubric or total weight 0 · "
                + "MANDATORY_CRITERION_FAILED · BELOW_PASS_THRESHOLD · ALL_REQUIREMENTS_MET.");
        }
        else if (type == typeof(Fire3D.Application.Learning.CriterionResult))
        {
            Enum(schema, "operator", CriterionOperators, "Comparison of value against threshold.");
            Enum(schema, "reason", CriterionReasons, "METRIC_UNAVAILABLE when the metric was not computed (the criterion fails); otherwise null.");
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
