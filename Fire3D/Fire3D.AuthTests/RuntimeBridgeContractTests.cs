using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Fire3D.Application.Learning;
using Fire3D.Infrastructure.Learning;
using Fire3D.Tests.Shared;
using Json.Schema;
using Xunit;

namespace Fire3D.AuthTests;

/// <summary>
/// Unity#5: the Mobile/Unity bridge contract (contracts/runtime/v1) is owned here. Every shared fixture is checked against the
/// JSON Schemas with a real validator, every invalid fixture must fail, and the event/complete/result shapes must match the BE DTOs
/// and validators, so the schemas cannot drift from the API.
/// </summary>
public sealed class RuntimeBridgeContractTests
{
    private static readonly string Root = Path.Combine(EditorContractFixtures.ContractDirectory, "..", "..", "runtime", "v1");
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
    private static readonly EvaluationOptions Evaluation = new() { OutputFormat = OutputFormat.List, RequireFormatValidation = true };
    private static readonly Lazy<Dictionary<string, JsonSchema>> Schemas = new(() =>
    {
        var build = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        // session-event first: bridge-message refers to it by relative $ref.
        return new[] { "session-event.schema.json", "launch.schema.json", "control.schema.json", "bridge-message.schema.json", "training-result.schema.json" }
            .ToDictionary(x => x, x => JsonSchema.FromText(File.ReadAllText(Path.Combine(Root, x)), build));
    });

    private static JsonElement Fixture(string kind, string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "fixtures", kind, file))).RootElement.Clone();
    private static JsonNode Index() => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "fixtures", "index.json")))!;
    private static bool Valid(string schema, JsonElement value) => Schemas.Value[schema].Evaluate(value, Evaluation).IsValid;

    public static TheoryData<string, string> ValidFixtures() => Rows("valid");
    public static TheoryData<string, string> InvalidFixtures() => Rows("invalid");
    private static TheoryData<string, string> Rows(string kind)
    {
        var data = new TheoryData<string, string>();
        foreach (var row in Index()[kind]!.AsArray()) data.Add(row!["file"]!.GetValue<string>(), row["schema"]!.GetValue<string>());
        return data;
    }

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void Valid_fixtures_satisfy_their_schema(string file, string schema) => Assert.True(Valid(schema, Fixture("valid", file)), $"{file} should satisfy {schema}");

    [Theory]
    [MemberData(nameof(InvalidFixtures))]
    public void Invalid_fixtures_are_refused_by_their_schema(string file, string schema) => Assert.False(Valid(schema, Fixture("invalid", file)), $"{file} should be refused by {schema}");

    [Fact]
    public void Index_lists_every_fixture_file_once_with_a_reason()
    {
        foreach (var kind in new[] { "valid", "invalid" })
        {
            var listed = Index()[kind]!.AsArray().Select(x => x!["file"]!.GetValue<string>()).ToList();
            var files = Directory.GetFiles(Path.Combine(Root, "fixtures", kind), "*.json").Select(Path.GetFileName).ToList();
            Assert.Equal(files.Order(), listed.Order());
        }
        Assert.All(Index()["invalid"]!.AsArray(), x => Assert.False(string.IsNullOrWhiteSpace(x!["why"]?.GetValue<string>())));
    }

    // The Events payload is what Mobile posts to events:batch unchanged: it must be a valid BE request, and the bad samples must
    // be refused by BE too.
    [Fact]
    public void Events_fixture_is_a_valid_events_batch_and_bad_metric_payloads_are_refused_by_the_backend()
    {
        static TrainingEventsRequest Batch(JsonElement message) => new(message.GetProperty("payload").Deserialize<List<TrainingEvent>>(Strict)!);
        Assert.Null(LearnerSessions.ValidateEvents(Batch(Fixture("valid", "bridge.events.json"))));
        foreach (var bad in new[] { "bridge.moved-negative.json", "bridge.exposure-missing-amount.json", "bridge.event-sequence-zero.json", "bridge.events-501.json" })
            Assert.NotNull(LearnerSessions.ValidateEvents(Batch(Fixture("invalid", bad))));
    }

    [Fact]
    public void Backend_event_and_result_dtos_serialize_to_the_shared_schemas()
    {
        var training = new TrainingEvent(Guid.NewGuid(), 7, "1", "Moved", DateTimeOffset.Parse("2026-10-18T08:00:00Z"), 35000, JsonDocument.Parse("""{"distanceMeters":3.5}""").RootElement);
        Assert.True(Valid("session-event.schema.json", JsonSerializer.SerializeToElement(training, Api)));
        var result = new TrainingResultView(Guid.NewGuid(), "Assessment", "NotPassed", "MANDATORY_CRITERION_FAILED", 50m, new string('b', 64),
            [new CriterionResult("exit", "reached_exit", JsonDocument.Parse("0").RootElement, "eq", 1m, true, 1m, false, null, "Thoát ra ngoài", null),
             new CriterionResult("time", "completion_time_seconds", null, "lte", 120m, false, 1m, false, "METRIC_UNAVAILABLE")],
            JsonDocument.Parse("""{"reached_exit":0}""").RootElement, 9, DateTime.Parse("2026-10-18T08:01:00Z").ToUniversalTime(), 70m);
        Assert.True(Valid("training-result.schema.json", JsonSerializer.SerializeToElement(result, Api)));
        // And back: the shared result fixtures are exactly the DTO (no unknown or renamed field).
        foreach (var file in new[] { "training-result.assessment.json", "training-result.learn.json" })
            Assert.NotNull(Fixture("valid", file).Deserialize<TrainingResultView>(Strict));
    }

    // The package manifest sample (fields the Unity worker writes) satisfies the fet3d.package/1 schema and the BE output validator.
    [Fact]
    public void Package_manifest_sample_satisfies_the_schema_and_the_backend_validator()
    {
        var folder = Path.Combine(Root, "..", "..", "package", "v1");
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(folder, "manifest.schema.json")), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        var manifest = File.ReadAllText(Path.Combine(folder, "fixtures", "manifest.valid.json"));
        Assert.True(schema.Evaluate(JsonDocument.Parse(manifest).RootElement, Evaluation).IsValid);
        Assert.Empty(Fire3D.Application.Ifc.PackageManifest.Validate(JsonNode.Parse(manifest), "Android"));
    }

    [Fact]
    public void Completed_maps_to_the_complete_request_the_backend_accepts()
    {
        var payload = Fixture("valid", "bridge.completed.json").GetProperty("payload");
        var request = new CompleteTrainingSessionRequest(payload.GetProperty("lastEventSequence").GetInt64(), payload.GetProperty("endReason").GetString()!);
        Assert.True(request.LastEventSequence >= 0 && request.EndReason is "Finished" or "Abandoned" or "TimedOut");
        // The schema's end reasons are exactly the ones the complete endpoint accepts.
        Assert.Equal(["Abandoned", "Finished", "TimedOut"], JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "bridge-message.schema.json")))!["$defs"]!["completed"]!["properties"]!["endReason"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).Order());
    }
}
