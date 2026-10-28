using System.Text.Json.Nodes;
using Fire3D.Application.Editor;
using Fire3D.Application.Learning;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;
using Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog;
using Fire3D.Infrastructure.Learning;
using Fire3D.Infrastructure.Scenarios;
using Fire3D.Tests.Shared;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // BE#183: a metric declared by the pinned runtime's catalog is scorable without a backend change; before it is declared, prepare refuses it.
    [PostgresFact]
    public async Task Runtime_declared_metric_is_prepared_and_counted_from_the_catalog()
    {
        var rubric = JsonNode.Parse("""
            {"schema_version":"1","pass_threshold":1,"criteria":[
             {"id":"exit","metric":"reached_exit","mandatory":true,"weight":1,"operator":"eq","threshold":1},
             {"id":"hose","metric":"hose_reel_used","mandatory":true,"weight":1,"operator":"gte","threshold":2}]}
            """)!.AsObject();
        var f = await PublishedTraining(rubric);
        var trainee = await SeedTrainee();
        var before = (string[])(await ScalarAsync("SELECT learner_rubric_metrics('1.0.0')"))!;
        Assert.Equal(EditorContract.CoreMetrics, before.Take(5));
        Assert.Subset(before.ToHashSet(), EditorContract.LearnerMetrics.ToHashSet());
        Assert.DoesNotContain("hose_reel_used", before);
        await WithLearnerRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new LearnerSessions(db, Options.Create(LearnerOptions));
            var caller = new LearnerCaller(trainee.Id, trainee.Family, null);
            var refused = await store.PrepareAsync(caller, new(f.Training, "Assessment", "1.0.0"), "hose-refused", default);
            Assert.Equal(("RUBRIC_NOT_SUPPORTED", 409), (refused.Error?.Code, refused.Error?.Status));

            await ExecuteAsync("""
                UPDATE runtime_compatibility_catalog SET capabilities=capabilities||'["extinguisher.hose_reel"]'::jsonb,
                 capability_contracts=capability_contracts||'{"extinguisher.hose_reel":{"version":"1.0.0","objectKinds":["Equipment"],"events":[{"type":"HoseReelUsed","metric":"hose_reel_used","payload":{"type":"object","additionalProperties":false,"required":["objectId"],"properties":{"objectId":{"type":"string","maxLength":128}}}}]}}'::jsonb
                 WHERE runtime_version='1.0.0';
                """);
            var sql = (string[])(await ScalarAsync("SELECT learner_rubric_metrics('1.0.0')"))!;
            await using var reader = BuildingContext(testConnection);
            var catalog = await new RuntimeCatalogReadStore(reader).GetActiveRuntimeAsync("1.0.0", default);
            Assert.NotNull(catalog);
            Assert.Equal(EditorContract.RuntimeMetrics(RuntimeCatalogContracts.For(catalog)).Keys, sql);
            Assert.Contains("hose_reel_used", sql);

            var id = await Running(store, caller, f.Training, "Assessment", "hose");
            var batch = (await store.RecordEventsAsync(caller, id, new([
                LearnerEvent(1, "HoseReelUsed", 1000, new { objectId = "hose-1" }),
                LearnerEvent(2, "HoseReelUsed", 1500, new { floor = 1 }),
                LearnerEvent(3, "HoseReelUsed", 2000, new { objectId = "hose-1" }),
                LearnerEvent(4, "ExitReached", 3000)]), default)).Value!;
            Assert.Equal("EVENT_PAYLOAD_INVALID", Assert.Single(batch.Conflicts).Code);
            Assert.Equal(200, (await store.CompleteAsync(caller, id, new(4, "Finished"), "hose-complete", default)).Value.Status);
            var result = (await store.ResultAsync(caller, id, default)).Value!;
            // The rejected event is not counted; the 2.0.x metrics stay present for rubrics written before the runtime declared metrics.
            Assert.Equal(2, result.Metrics.GetProperty("hose_reel_used").GetInt32());
            Assert.Equal(0, result.Metrics.GetProperty("used_elevator").GetInt32());
            Assert.Equal(("Passed", "ALL_REQUIREMENTS_MET"), (result.Outcome, result.Reason));
        });
    }
}

public sealed class RuntimeRubricMetricTests
{
    private const string HoseReel = """
        {"version":"1.0.0","objectKinds":["Equipment"],"events":[{"type":"HoseReelUsed","metric":"hose_reel_used",
         "payload":{"type":"object","additionalProperties":false,"required":["objectId"],"properties":{"objectId":{"type":"string","maxLength":128}}}}]}
        """;

    private static JsonObject Batch1() => JsonNode.Parse(File.ReadAllText(Path.Combine(EditorContractFixtures.ContractDirectory, "fixtures", "runtime-catalog.batch1.json")))!.AsObject();

    private static JsonObject With(JsonObject catalog, string id, string contract)
    {
        catalog["capabilities"]!.AsArray().Add(id);
        catalog["capabilityContracts"]![id] = JsonNode.Parse(contract);
        return catalog;
    }

    private static (IReadOnlyDictionary<string, CapabilityContract> Contracts, List<ScenarioDraftValidationIssue> Issues) Parse(JsonObject catalog)
    {
        var issues = new List<ScenarioDraftValidationIssue>();
        return (CapabilityContracts.Parse(catalog["capabilities"], catalog["capabilityContracts"], issues), issues);
    }

    private static JsonObject Draft(string metric)
    {
        var state = EditorContractFixtures.State();
        state["rubric"]!["criteria"]![0]!["metric"] = metric;
        return state;
    }

    [Fact]
    public void Runtime_2_0_catalog_keeps_the_previous_metrics()
    {
        var (contracts, issues) = Parse(Batch1());
        Assert.Empty(issues);
        var metrics = EditorContract.RuntimeMetrics(contracts);
        Assert.Equal(EditorContract.CoreMetrics, metrics.Keys.Take(5));
        Assert.Equal(EditorContract.LearnerMetrics.Order(StringComparer.Ordinal), metrics.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("AlarmRaised", metrics["alarm_raised"]);
        Assert.Null(metrics["reached_exit"]);
        Assert.Equal(EditorContract.LearnerMetrics, RubricContract.Catalog.Metrics.Select(x => x.Id));
    }

    [Fact]
    public void Declared_metric_is_offered_only_by_its_runtime()
    {
        var (contracts, issues) = Parse(With(Batch1(), "extinguisher.hose_reel", HoseReel));
        Assert.Empty(issues);
        var metrics = EditorContract.RuntimeMetrics(contracts);
        Assert.Equal("HoseReelUsed", metrics["hose_reel_used"]);
        Assert.Equal(16, metrics.Count);
        Assert.Equal("Number of accepted HoseReelUsed events (rejected events are not counted).",
            RubricContract.CatalogFor(metrics).Metrics.Single(x => x.Id == "hose_reel_used").Description);

        // A draft is checked against its own runtime: the metric is accepted on the declaring runtime only.
        var withHose = Draft("hose_reel_used");
        Assert.DoesNotContain(ScenarioStateV1Validator.ValidateComplete(withHose, contracts), x => x.Code == "RUBRIC_METRIC_UNSUPPORTED");
        Assert.Contains(ScenarioStateV1Validator.ValidateComplete(withHose, Parse(Batch1()).Contracts),
            x => x.Code == "RUBRIC_METRIC_UNSUPPORTED" && x.Path == "$.rubric.criteria[0].metric");
        Assert.Contains(ScenarioStateV1Validator.ValidateRubricDocument(withHose["rubric"]), x => x.Code == "RUBRIC_METRIC_UNSUPPORTED");
        Assert.Empty(ScenarioStateV1Validator.ValidateRubricDocument(withHose["rubric"], metrics.Keys.ToArray()));
    }

    [Fact]
    public void Union_of_active_runtimes_lists_core_metrics_first()
    {
        RuntimeCatalogDto Runtime(string version, JsonObject catalog) => new(version, "1", "1", catalog["capabilities"]!, catalog["capabilityContracts"]!.AsObject());
        var union = RuntimeCatalogContracts.Metrics([Runtime("2.0.2", Batch1()), Runtime("2.1.0", With(Batch1(), "extinguisher.hose_reel", HoseReel))]);
        Assert.Equal(EditorContract.CoreMetrics, union.Keys.Take(5));
        Assert.Equal(union.Keys.Skip(5).Order(StringComparer.Ordinal), union.Keys.Skip(5));
        Assert.Contains("hose_reel_used", union.Keys);
        Assert.Equal(EditorContract.CoreMetrics, RuntimeCatalogContracts.Metrics([]).Keys);
    }

    [Theory]
    [InlineData("""{"type":"HoseReelUsed","metric":"HoseReel"}""", "STRING_FORMAT_INVALID")]
    [InlineData("""{"type":"HoseReelUsed","metric":"1hose"}""", "STRING_FORMAT_INVALID")]
    [InlineData("""{"type":"HoseReelUsed","metric":7}""", "TYPE_INVALID")]
    [InlineData("""{"type":"HoseReelUsed","metric":"reached_exit"}""", "METRIC_CONFLICT")]
    [InlineData("""{"type":"HoseReelUsed","metric":"alarm_raised"}""", "METRIC_CONFLICT")]
    public void Invalid_event_metrics_are_reported(string ev, string code)
    {
        var catalog = JsonNode.Parse("""{"capabilities":["extinguisher.hose_reel"],"capabilityContracts":{"extinguisher.hose_reel":{"version":"1.0.0","objectKinds":["Equipment"],"events":[""" + ev + "]}}}")!.AsObject();
        var (contracts, issues) = Parse(catalog);
        Assert.Contains(issues, x => x.Code == code);
        Assert.Empty(contracts);
    }

    [Fact]
    public void Too_long_metric_is_rejected()
    {
        var catalog = JsonNode.Parse("""{"capabilities":["extinguisher.hose_reel"],"capabilityContracts":{"extinguisher.hose_reel":{"version":"1.0.0","objectKinds":["Equipment"],"events":[{"type":"HoseReelUsed"}]}}}""")!.AsObject();
        catalog["capabilityContracts"]!["extinguisher.hose_reel"]!["events"]![0]!["metric"] = new string('a', EditorContract.MetricMaxLength + 1);
        Assert.NotEmpty(Parse(catalog).Issues);
        catalog["capabilityContracts"]!["extinguisher.hose_reel"]!["events"]![0]!["metric"] = new string('a', EditorContract.MetricMaxLength);
        Assert.Empty(Parse(catalog).Issues);
    }

    [Fact]
    public void One_metric_counts_one_event_type()
    {
        // Two capabilities may repeat the same metric for the same event (blankets on a person and on an item).
        const string blanket = """{"version":"1.0.0","objectKinds":["Equipment"],"events":[{"type":"BlanketCovered","metric":"blanket_covered"}]}""";
        var same = With(With(Batch1(), "blanket.fire", blanket), "item.blanket", blanket);
        Assert.Empty(Parse(same).Issues);

        // The same metric for another type, or another metric for the same type, removes the later capability.
        var otherType = With(With(Batch1(), "blanket.fire", blanket), "window.openable",
            """{"version":"1.0.0","objectKinds":["Equipment"],"events":[{"type":"WindowOpened","metric":"blanket_covered"}]}""");
        var (contracts, issues) = Parse(otherType);
        Assert.Equal("METRIC_CONFLICT", Assert.Single(issues).Code);
        Assert.True(contracts.ContainsKey("blanket.fire"));
        Assert.False(contracts.ContainsKey("window.openable"));

        var otherMetric = With(With(Batch1(), "blanket.fire", blanket), "item.blanket",
            """{"version":"1.0.0","objectKinds":["Equipment"],"events":[{"type":"BlanketCovered","metric":"item_covered"}]}""");
        (contracts, issues) = Parse(otherMetric);
        Assert.Equal("METRIC_CONFLICT", Assert.Single(issues).Code);
        Assert.False(contracts.ContainsKey("item.blanket"));
    }
}
