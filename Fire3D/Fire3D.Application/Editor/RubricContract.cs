using System.Text.Json.Nodes;

namespace Fire3D.Application.Editor;

public sealed record RubricMetric(string Id,string Description);
/// <summary>label is the short name shown to learners next to the result; description explains the criterion.</summary>
public sealed record RubricCriterionShape(IReadOnlyList<string> RequiredFields,IReadOnlyList<string> OptionalFields,string IdPattern,int IdMaxLength,int DescriptionMaxLength,decimal WeightMinimum,int LabelMaxLength);
/// <summary>What the backend accepts for scenario rubrics, Library rubric samples and AI drafts. No pass threshold, weight or criterion is suggested.</summary>
public sealed record RubricCatalog(IReadOnlyList<string> SupportedSchemaVersions,string DefaultSchemaVersion,IReadOnlyList<RubricMetric> Metrics,
    IReadOnlyList<string> Operators,RubricCriterionShape Criterion,int MaxCriteria,IReadOnlyList<string> RubricFields);

/// <summary>
/// Single registry for the rubric contract. The first canonical schema version is the string "1"; the number 1, "v1" or "1.0" are not
/// aliases. Stored snapshots are never rewritten; an unrecognised legacy version stays readable but cannot be snapshotted or submitted again.
/// </summary>
public static class RubricContract
{
    public const string DefaultSchemaVersion="1";
    public static readonly string[] SupportedSchemaVersions=[DefaultSchemaVersion];
    public static readonly string[] Operators=["gte","lte","eq"];
    public const int MaxCriteria=100;
    public const int CriterionIdMaxLength=128;
    public const int DescriptionMaxLength=1000;
    public const int LabelMaxLength=200;
    public static readonly string[] CriterionRequiredFields=["id","metric","mandatory","weight","operator","threshold"];
    public static readonly string[] CriterionOptionalFields=["label","description"];
    public static readonly string[] RubricFields=["schema_version","pass_threshold","criteria"];
    public static bool IsSupportedVersion(JsonNode? node)=>node is JsonValue value&&value.TryGetValue<string>(out var text)&&SupportedSchemaVersions.Contains(text,StringComparer.Ordinal);
    public const string UnsupportedCode="RUBRIC_SCHEMA_VERSION_UNSUPPORTED";
    public static string UnsupportedMessage=>$"Use rubric schema_version \"{DefaultSchemaVersion}\" (string); supported: {string.Join(", ",SupportedSchemaVersions)}.";

    private static readonly Dictionary<string,string> MetricDescriptions=new()
    {
        ["reached_exit"]="1 when the trainee reached an exit, otherwise 0.",
        ["completion_time_seconds"]="Seconds until the first exit (or the last event when no exit was reached).",
        ["wrong_exits"]="Number of WrongExit events.",
        ["hazard_exposure"]="Sum of HazardExposure amounts.",
        ["distance_meters"]="Sum of Moved distances in metres."
    };
    public static RubricCatalog Catalog { get; }=new(SupportedSchemaVersions,DefaultSchemaVersion,
        EditorContract.LearnerMetrics.Select(x=>new RubricMetric(x,MetricDescriptions.GetValueOrDefault(x)??
            $"Number of accepted {EditorContract.EventCountMetrics[x]} events (rejected events are not counted).")).ToArray(),Operators,
        new(CriterionRequiredFields,CriterionOptionalFields,EditorContract.IdPattern().ToString(),CriterionIdMaxLength,DescriptionMaxLength,0m,LabelMaxLength),
        MaxCriteria,RubricFields);
}
