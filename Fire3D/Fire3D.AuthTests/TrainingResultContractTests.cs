using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Learning;
using Fire3D.Infrastructure.Learning;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // Issue #96: result details carry the pinned rubric's labels and pass threshold; history lists only real statuses and the
    // Trainee gets the Buildings of their own sessions for the filter.
    [PostgresFact]
    public async Task Training_result_contract_has_labels_threshold_and_trainee_buildings()
    {
        var rubric = ScorableRubric.DeepClone().AsObject();
        rubric["criteria"]![0]!["label"] = "Ra được lối thoát";
        rubric["criteria"]![0]!["description"] = "Tới một lối thoát an toàn.";
        var f = await PublishedTraining(rubric);
        var trainee = await SeedTrainee(); var other = await SeedTrainee();
        await WithLearnerRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new LearnerSessions(db, Options.Create(LearnerOptions));
            var caller = new LearnerCaller(trainee.Id, trainee.Family, null);
            var assessment = await Running(store, caller, f.Training, "Assessment", "result-assessment");
            await store.RecordEventsAsync(caller, assessment, new([LearnerEvent(1, "ExitReached", 30000)]), default);
            Assert.Equal(200, (await store.CompleteAsync(caller, assessment, new(1, "Finished"), "result-assessment-complete", default)).Value.Status);
            var result = (await store.ResultAsync(caller, assessment, default)).Value!;
            Assert.Equal(50m, result.PassThresholdPercent);
            var exit = result.Criteria!.Single(c => c.Id == "exit");
            Assert.Equal(("Ra được lối thoát", "Tới một lối thoát an toàn."), (exit.Label, exit.Description));
            var fast = result.Criteria!.Single(c => c.Id == "fast");
            Assert.Null(fast.Label); Assert.Null(fast.Reason);

            var learn = await Running(store, caller, f.Training, "Learn", "result-learn");
            await store.CompleteAsync(caller, learn, new(0, "Finished"), "result-learn-complete", default);
            var learnResult = (await store.ResultAsync(caller, learn, default)).Value!;
            Assert.Equal(("NotAssessed", "MODE_NOT_ASSESSED"), (learnResult.Outcome, learnResult.Reason));
            Assert.Null(learnResult.Criteria); Assert.Null(learnResult.PassThresholdPercent);
            // The rubric helper answers only for the session owner.
            Assert.True(await ScalarAsync($"SELECT learner_result_rubric('{other.Id}','{assessment}')") is null or DBNull);

            var buildings = (await store.BuildingsAsync(caller, 1, 20, default)).Value!;
            var building = Assert.Single(buildings.Items);
            Assert.Equal((f.Building, 2), (building.Id, building.SessionCount));
            Assert.Equal(0, (await store.BuildingsAsync(new(other.Id, other.Family, null), 1, 20, default)).Value!.TotalCount);
            Assert.Equal("VALIDATION_ERROR", (await store.BuildingsAsync(caller, 0, 20, default)).Error?.Code);
            Assert.Equal("FORBIDDEN", (await store.BuildingsAsync(new(f.Owner, f.OwnerFamily, null), 1, 20, default)).Error?.Code);

            // Statuses never written for v7 sessions are not filter values any more.
            foreach (var status in new[] { "Crashed", "Aborted", "Abandoned", "ScenarioUnsurvivable", "CompletedWithSupersededRelease" })
                Assert.Equal("VALIDATION_ERROR", (await store.ListAsync(caller, new(status, null, null, null, null), default)).Error?.Code);
            Assert.Equal(2, (await store.ListAsync(caller, new("Completed", null, null, null, null), default)).Value!.TotalCount);
            Assert.Equal("VALIDATION_ERROR", (string?)(await ScalarAsync($"SELECT learner_session_list_gate('{trainee.Id}','{trainee.Family}','{{\"status\":\"Crashed\"}}')->>'code'")));
        });
    }

    [PostgresFact]
    public async Task Training_OpenAPI_publishes_learner_enums_and_codes()
    {
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var root = doc.RootElement; var schemas = root.GetProperty("components").GetProperty("schemas");
        string[] Values(string schema, string property) => schemas.GetProperty(schema).GetProperty("properties").GetProperty(property).GetProperty("enum")
            .EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray();
        Assert.Equal(["Launching", "Running", "AwaitingSync", "Completed"], Values("TrainingHistoryItem", "status"));
        Assert.Equal(["Passed", "NotPassed", "Incomplete", "NotAssessed"], Values("TrainingHistoryItem", "resultStatus"));
        Assert.Equal(["Finished", "TimedOut", "Abandoned"], Values("TrainingHistoryItem", "endReason"));
        Assert.Contains("BELOW_PASS_THRESHOLD", Values("TrainingResultView", "reason"));
        Assert.Equal(["METRIC_UNAVAILABLE"], Values("CriterionResult", "reason"));
        Assert.Contains("Prepared", Values("TrainingSessionView", "status"));
        Assert.True(schemas.GetProperty("TrainingResultView").GetProperty("properties").TryGetProperty("passThresholdPercent", out _));
        var paths = root.GetProperty("paths");
        var list = paths.GetProperty("/api/training/sessions").GetProperty("get").GetProperty("parameters").EnumerateArray().ToList();
        Assert.Equal(["Learn", "Guided", "Assessment"], list.Single(p => p.GetProperty("name").GetString() == "mode").GetProperty("schema").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(4, list.Single(p => p.GetProperty("name").GetString() == "status").GetProperty("schema").GetProperty("enum").GetArrayLength());
        foreach (var path in new[] { "/api/training/sessions/{id}/result", "/api/training/sessions/{id}/heartbeat" })
            Assert.True(paths.GetProperty(path).EnumerateObject().First().Value.GetProperty("responses").TryGetProperty("403", out _), path);
        Assert.True(paths.TryGetProperty("/api/training/buildings", out _));
        Assert.Contains("label", schemas.GetRawText());
    }
}
