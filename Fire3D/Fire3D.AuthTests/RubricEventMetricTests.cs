using System.Text.Json.Nodes;
using Fire3D.Application.Editor;
using Fire3D.Application.Learning;
using Fire3D.Infrastructure.Learning;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // BE#101: rubric criteria on Docs IFC 6.1 player-action events count accepted events only; a rejected event never changes a score.
    [PostgresFact]
    public async Task Event_rubric_metrics_count_accepted_player_actions_only()
    {
        var rubric = JsonNode.Parse("""
            {"schema_version":"1","pass_threshold":0.6,"criteria":[
             {"id":"exit","metric":"reached_exit","mandatory":true,"weight":1,"operator":"eq","threshold":1},
             {"id":"alarm","metric":"alarm_raised","mandatory":false,"weight":1,"operator":"gte","threshold":1},
             {"id":"no-elevator","metric":"used_elevator","mandatory":true,"weight":1,"operator":"eq","threshold":0}]}
            """)!.AsObject();
        Assert.Empty(ScenarioStateV1Validator.ValidateRubricDocument(rubric));
        // A runtime without a catalog row still offers the core and 2.0.x metrics (BE#183 keeps them for every runtime).
        Assert.Equal(EditorContract.LearnerMetrics.Order(StringComparer.Ordinal), ((string[])(await ScalarAsync("SELECT learner_rubric_metrics('0.0.0')"))!).Order(StringComparer.Ordinal));
        var f = await PublishedTraining(rubric);
        var trainee = await SeedTrainee();
        await DeclareAlarmEvent();
        await WithLearnerRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new LearnerSessions(db, Options.Create(LearnerOptions));
            var caller = new LearnerCaller(trainee.Id, trainee.Family, null);

            // UsedElevator is not declared by runtime 1.0.0 yet: it is rejected, so the no-elevator criterion still passes.
            var id = await Running(store, caller, f.Training, "Assessment", "metric-a");
            var batch = (await store.RecordEventsAsync(caller, id, new([
                LearnerEvent(1, "AlarmRaised", 1000, new { objectId = "call-1" }),
                LearnerEvent(2, "AlarmRaised", 1500, new { floor = 2 }),
                LearnerEvent(3, "UsedElevator", 2000, new { objectId = "lift-1" }),
                LearnerEvent(4, "ExitReached", 3000)]), default)).Value!;
            Assert.Equal(["EVENT_PAYLOAD_INVALID", "EVENT_TYPE_UNSUPPORTED"], batch.Conflicts.Select(c => c.Code));
            Assert.Equal(200, (await store.CompleteAsync(caller, id, new(4, "Finished"), "metric-a-complete", default)).Value.Status);
            var result = (await store.ResultAsync(caller, id, default)).Value!;
            Assert.Equal(1, result.Metrics.GetProperty("alarm_raised").GetInt32());
            Assert.Equal(0, result.Metrics.GetProperty("used_elevator").GetInt32());
            Assert.Equal(0, result.Metrics.GetProperty("npc_guided").GetInt32());
            Assert.Equal("Passed", result.Outcome);

            // Once the runtime declares the elevator, taking it fails the mandatory criterion.
            await ExecuteAsync("""
                UPDATE runtime_compatibility_catalog SET capabilities=capabilities||'["transport.elevator"]'::jsonb,
                 capability_contracts=capability_contracts||'{"transport.elevator":{"version":"1.0.0","objectKinds":["Equipment"],"events":[{"type":"UsedElevator","payload":{"type":"object","additionalProperties":false,"required":["objectId"],"properties":{"objectId":{"type":"string","maxLength":128}}}}]}}'::jsonb
                 WHERE runtime_version='1.0.0';
                """);
            var second = await Running(store, caller, f.Training, "Assessment", "metric-b");
            var accepted = (await store.RecordEventsAsync(caller, second, new([
                LearnerEvent(1, "UsedElevator", 1000, new { objectId = "lift-1" }),
                LearnerEvent(2, "ExitReached", 2000)]), default)).Value!;
            Assert.Empty(accepted.Conflicts);
            Assert.Equal(200, (await store.CompleteAsync(caller, second, new(2, "Finished"), "metric-b-complete", default)).Value.Status);
            var failed = (await store.ResultAsync(caller, second, default)).Value!;
            Assert.Equal(1, failed.Metrics.GetProperty("used_elevator").GetInt32());
            Assert.Equal(("NotPassed", "MANDATORY_CRITERION_FAILED"), (failed.Outcome, failed.Reason));
        });
    }
}
