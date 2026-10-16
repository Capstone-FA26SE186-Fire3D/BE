using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Editor;
using Fire3D.Application.Learning;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;
using Fire3D.Infrastructure.Learning;
using Fire3D.Infrastructure.Scenarios;
using Fire3D.Tests.Shared;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private const string AlarmContract = """
        {"alarm.call_point":{"version":"1.0.0","objectKinds":["Equipment"],"events":[{"type":"AlarmRaised",
          "payload":{"type":"object","additionalProperties":false,"required":["objectId"],"properties":{"objectId":{"type":"string","maxLength":128}}}}]}}
        """;

    // SCENARIO-02: the pinned runtime declares AlarmRaised; anything else outside the core events is rejected.
    private Task DeclareAlarmEvent() => ExecuteAsync($"""
        INSERT INTO runtime_compatibility_catalog(id,runtime_version,protocol_version,manifest_schema_version,capabilities)
         SELECT gen_random_uuid(),'1.0.0','1','1','[]' WHERE NOT EXISTS(SELECT 1 FROM runtime_compatibility_catalog WHERE runtime_version='1.0.0');
        UPDATE runtime_compatibility_catalog SET capabilities=CASE WHEN capabilities ? 'alarm.call_point' THEN capabilities ELSE capabilities||'["alarm.call_point"]'::jsonb END,
         capability_contracts=capability_contracts||'{AlarmContract}'::jsonb WHERE runtime_version='1.0.0';
        """);

    [PostgresFact]
    public async Task Learner_events_outside_the_runtime_allow_list_are_rejected_without_stalling_completion()
    {
        var f = await PublishedTraining(ScorableRubric);
        var trainee = await SeedTrainee();
        await DeclareAlarmEvent();
        await WithLearnerRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new LearnerSessions(db, Options.Create(LearnerOptions));
            var caller = new LearnerCaller(trainee.Id, trainee.Family, null);
            var id = await Running(store, caller, f.Training, "Assessment", "allow");
            var unknown = LearnerEvent(2, "Teleported", 2000);
            var batch = (await store.RecordEventsAsync(caller, id, new([
                LearnerEvent(1, "AlarmRaised", 1000, new { objectId = "call-1" }),
                unknown,
                LearnerEvent(3, "AlarmRaised", 3000, new { floor = 2 }),
                LearnerEvent(4, "ExitReached", 4000)]), default)).Value!;
            Assert.Equal(2, batch.Accepted.Count);
            Assert.Equal(["EVENT_TYPE_UNSUPPORTED", "EVENT_PAYLOAD_INVALID"], batch.Conflicts.Select(c => c.Code));
            // Rejected events still fill their sequence: nothing is missing and completion does not wait for them.
            Assert.Equal(4, batch.AcknowledgedSequence);
            Assert.Empty(batch.MissingRanges);
            Assert.Equal(2L, await ScalarAsync($"SELECT count(*) FROM session_events WHERE session_id='{id}' AND rejection_code IS NOT NULL"));

            // Resending a rejected event repeats the rejection and never turns it into an accepted event.
            var again = (await store.RecordEventsAsync(caller, id, new([unknown]), default)).Value!;
            Assert.Empty(again.Duplicates);
            Assert.Equal("EVENT_TYPE_UNSUPPORTED", Assert.Single(again.Conflicts).Code);

            var done = await store.CompleteAsync(caller, id, new(4, "Finished"), "allow-complete", default);
            Assert.Equal(200, done.Value.Status);
            var result = (await store.ResultAsync(caller, id, default)).Value!;
            Assert.Equal("Passed", result.Outcome);
            Assert.Equal(4, result.Metrics.GetProperty("completion_time_seconds").GetDecimal());

            // Without an exit the time is the last received event, rejected or not: an undeclared event still shows the session went on.
            var learn = await Running(store, caller, f.Training, "Learn", "allow-learn");
            await store.RecordEventsAsync(caller, learn, new([LearnerEvent(1, "Moved", 1000, new { distanceMeters = 1 }), LearnerEvent(2, "Teleported", 9000)]), default);
            Assert.Equal(200, (await store.CompleteAsync(caller, learn, new(2, "TimedOut"), "allow-learn-complete", default)).Value.Status);
            Assert.Equal(9, (await store.ResultAsync(caller, learn, default)).Value!.Metrics.GetProperty("completion_time_seconds").GetDecimal());
        });
    }

    // Option (a): an Assessment on a rubric that cannot be scored is refused at prepare instead of ending NotAssessed.
    [PostgresFact]
    public async Task Assessment_prepare_refuses_a_rubric_without_positive_weight()
    {
        var unscorable = JsonNode.Parse("""
            {"schema_version":"1","pass_threshold":0.5,"criteria":[{"id":"exit","metric":"reached_exit","mandatory":false,"weight":0,"operator":"eq","threshold":1}]}
            """)!.AsObject();
        var f = await PublishedTraining(unscorable);
        var trainee = await SeedTrainee();
        await WithLearnerRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new LearnerSessions(db, Options.Create(LearnerOptions));
            var caller = new LearnerCaller(trainee.Id, trainee.Family, null);
            Assert.Equal("RUBRIC_NOT_SUPPORTED", (await store.PrepareAsync(caller, new(f.Training, "Assessment", "1.0.0"), "zero-weight", default)).Error?.Code);
            Assert.True((await store.PrepareAsync(caller, new(f.Training, "Learn", "1.0.0"), "zero-weight-learn", default)).IsSuccess);
        });
    }

    [PostgresFact]
    public async Task Event_payload_checks_match_the_csharp_parameter_rules()
    {
        const string schema = """{"type":"object","additionalProperties":false,"properties":{"n":{"type":"number","enum":[1]},"i":{"type":"integer"},"s":{"type":"string","maxLength":3}}}""";
        async Task<bool> Valid(string payload) => (bool)(await ScalarAsync($"SELECT capability_payload_valid('{schema}'::jsonb,'{payload}'::jsonb)"))!;
        Assert.True(await Valid("""{"n":1,"i":5,"s":"ab"}"""));
        Assert.False(await Valid("""{"n":1.0}"""));                      // enum compares JSON text
        Assert.False(await Valid("""{"i":1234567890123456789}"""));      // beyond a 64-bit integer token
        Assert.False(await Valid("""{"s":"\u00a0\u3000"}"""));          // Unicode white space is blank
        Assert.False(await Valid("""{"s":"a\u0007"}"""));               // control character
        Assert.False(await Valid("""{"s":"😀😀"}"""));                     // 4 UTF-16 units > maxLength 3
        Assert.True(await Valid("""{"s":"😀a"}"""));
    }

    [PostgresFact]
    public async Task Playtest_events_use_the_same_runtime_allow_list()
    {
        var f = await SeedPlaytestRuntime();
        await DeclareAlarmEvent();
        await WithPlaytestRuntimeGates(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new PlaytestLifecycle(db, Options.Create(HandoffOptions));
            var id = (await store.PrepareAsync(f.Owner, f.Web, f.Building, f.Scenario, new(f.Revision, ScenarioVersionId: f.Version), "allow-prepare", default)).Value!.Id;
            var started = await store.StartAsync(f.Owner, f.Web, id, new("1.0.0"), "allow-start", default);
            Assert.True(started.IsSuccess, started.Error?.Code);
            Assert.True((await store.LaunchedAsync(f.Owner, f.Web, id, new(started.Value!.Generation), default)).IsSuccess);

            var unknown = Event(2, type: "move");
            var batch = (await store.RecordEventsAsync(f.Owner, f.Web, id, new([Event(1), unknown, Event(3, payload: "{\"objectId\":\"call-1\"}", type: "AlarmRaised")]), default)).Value!;
            Assert.Equal(2, batch.Accepted.Count);
            Assert.Equal("EVENT_TYPE_UNSUPPORTED", Assert.Single(batch.Conflicts).Code);
            Assert.Equal(3, batch.AcknowledgedSequence);
            Assert.Equal("EVENT_TYPE_UNSUPPORTED", Assert.Single((await store.RecordEventsAsync(f.Owner, f.Web, id, new([unknown]), default)).Value!.Conflicts).Code);
            Assert.True((await store.CompleteAsync(f.Owner, f.Web, id, new(3), "allow-complete", default)).IsSuccess);
        });
    }
}

public sealed class CapabilityEventContractTests
{
    private static JsonObject Batch1() => JsonNode.Parse(File.ReadAllText(Path.Combine(EditorContractFixtures.ContractDirectory, "fixtures", "runtime-catalog.batch1.json")))!.AsObject();

    private static (IReadOnlyDictionary<string, CapabilityContract> Contracts, List<ScenarioDraftValidationIssue> Issues) Parse(JsonObject catalog)
    {
        var issues = new List<ScenarioDraftValidationIssue>();
        return (CapabilityContracts.Parse(catalog["capabilities"], catalog["capabilityContracts"], issues), issues);
    }

    [Fact]
    public void Batch1_catalog_declares_events_and_actions_for_every_capability()
    {
        var catalog = Batch1();
        var (contracts, issues) = Parse(catalog);
        Assert.Empty(issues);
        Assert.Equal(16, contracts.Count);
        Assert.Equal(["CheckedDoorHeat", "ClosedDoor"], contracts["door.exit"].Events!.Select(x => x.Type));
        Assert.Equal(["AlarmRaised"], contracts["alarm.call_point"].Events!.Select(x => x.Type));
        // The sprinkler effect parameters have no defaults: a scenario object must set every one of them.
        var sprinkler = contracts["suppression.sprinkler"].Parameters!;
        Assert.Equal(5, sprinkler["required"]!.AsArray().Count);
        Assert.Equal(["Active", "Disabled", "Faulty"], sprinkler["properties"]!["state"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Null(sprinkler["properties"]!["state"]!["default"]);
    }

    [Fact]
    public void Rubric_threshold_is_a_ratio_and_some_weight_is_positive()
    {
        var rubric = JsonNode.Parse("""
            {"schema_version":"1","pass_threshold":70,"criteria":[{"id":"exit","metric":"reached_exit","mandatory":true,"weight":0,"operator":"eq","threshold":1}]}
            """);
        var codes = ScenarioStateV1Validator.ValidateRubricDocument(rubric).Select(x => $"{x.Code} {x.Path}").ToList();
        Assert.Equal(["NUMBER_OUT_OF_RANGE $.rubric.pass_threshold", "RUBRIC_WEIGHT_REQUIRED $.rubric.criteria"], codes);
        rubric!["pass_threshold"] = 0.7; rubric["criteria"]![0]!["weight"] = 1;
        Assert.Empty(ScenarioStateV1Validator.ValidateRubricDocument(rubric));
    }

    [Theory]
    [InlineData("""{"type":"ExitReached"}""", "EVENT_TYPE_RESERVED")]
    [InlineData("""{"type":"alarm_raised"}""", "STRING_FORMAT_INVALID")]
    [InlineData("""{"type":"AlarmRaised","priority":1}""", "FIELD_UNKNOWN")]
    [InlineData("""{"type":"AlarmRaised","payload":{"type":"object","additionalProperties":true,"properties":{}}}""", "VALUE_UNSUPPORTED")]
    public void Invalid_capability_events_are_reported(string ev, string code)
    {
        var catalog = JsonNode.Parse("""{"capabilities":["alarm.call_point"],"capabilityContracts":{"alarm.call_point":{"version":"1.0.0","objectKinds":["Equipment"],"events":[""" + ev + "]}}}")!.AsObject();
        var (contracts, issues) = Parse(catalog);
        Assert.Contains(issues, x => x.Code == code);
        Assert.Empty(contracts);
    }

    [Fact]
    public void Actions_name_declared_events_and_a_shared_event_keeps_one_payload()
    {
        var undeclared = JsonNode.Parse("""{"capabilities":["door.standard"],"capabilityContracts":{"door.standard":{"version":"1.0.0","objectKinds":["Equipment"],"actions":[{"id":"close","event":"ClosedDoor"},{"id":"enter","event":"ExitReached"}]}}}""")!.AsObject();
        Assert.Equal("EVENT_NOT_DECLARED", Assert.Single(Parse(undeclared).Issues).Code);

        var catalog = Batch1();
        catalog["capabilityContracts"]!["door.fire"]!["events"]![1]!["payload"] = JsonNode.Parse("""{"type":"object","additionalProperties":false,"properties":{"objectId":{"type":"string"},"slammed":{"type":"boolean"}}}""");
        var (contracts, issues) = Parse(catalog);
        Assert.Equal("EVENT_SCHEMA_CONFLICT", Assert.Single(issues).Code);
        Assert.False(contracts.ContainsKey("door.fire"));
        Assert.True(contracts.ContainsKey("door.exit"));
    }
}
