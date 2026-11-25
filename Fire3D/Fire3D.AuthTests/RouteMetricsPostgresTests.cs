using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Learning;
using Fire3D.Infrastructure.Learning;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

// BE#216 (FR-PLAN-10): step_count and route_efficiency from RouteBaseline and Moved.steps; without telemetry a criterion is not evaluable.
public sealed partial class AuthIntegrationTests
{
    private static readonly JsonObject RouteRubric = JsonNode.Parse("""
        {"schema_version":"1","pass_threshold":0.5,"criteria":[
          {"id":"exit","metric":"reached_exit","mandatory":true,"weight":1,"operator":"eq","threshold":1},
          {"id":"route","metric":"route_efficiency","mandatory":true,"weight":1,"operator":"gte","threshold":0.75},
          {"id":"steps","metric":"step_count","mandatory":false,"weight":1,"operator":"lte","threshold":200}]}
        """)!.AsObject();

    private async Task<TrainingResultView> RouteSession(Guid training, params TrainingEvent[] events)
    {
        var trainee = await SeedTrainee();
        TrainingResultView? result = null;
        await WithLearnerRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new LearnerSessions(db, Options.Create(LearnerOptions));
            var caller = new LearnerCaller(trainee.Id, trainee.Family, null);
            var id = await Running(store, caller, training, "Assessment", "route-" + trainee.Id);
            var all = events.Append(LearnerEvent(events.Length + 1, "ExitReached", 90_000)).ToList();
            var recorded = await store.RecordEventsAsync(caller, id, new(all), default);
            Assert.True(recorded.IsSuccess, recorded.Error?.Code);
            Assert.Empty(recorded.Value!.Conflicts);
            var completed = await store.CompleteAsync(caller, id, new(all.Count, "Finished"), "route-complete-" + trainee.Id, default);
            Assert.True(completed.IsSuccess, completed.Error?.Code);
            result = (await store.ResultAsync(caller, id, default)).Value!;
        });
        return result!;
    }

    [PostgresFact]
    public async Task Route_efficiency_and_step_count_score_from_runtime_telemetry_and_are_skipped_without_it()
    {
        var f = await PublishedTraining(RouteRubric);

        // Optimal 40 m, walked 50 m in 65 steps: efficiency 0.8, all criteria pass.
        var walked = await RouteSession(f.Training,
            LearnerEvent(1, "RouteBaseline", 0, new { optimalDistanceMeters = 40 }),
            LearnerEvent(2, "Moved", 30_000, new { distanceMeters = 30, steps = 40 }),
            LearnerEvent(3, "Moved", 60_000, new { distanceMeters = 20, steps = 25 }));
        Assert.Equal(0.8m, walked.Metrics.GetProperty("route_efficiency").GetDecimal());
        Assert.Equal(65m, walked.Metrics.GetProperty("step_count").GetDecimal());
        Assert.Equal(40m, walked.Metrics.GetProperty("optimal_distance_meters").GetDecimal());
        Assert.Equal(("Passed", 100m), (walked.Outcome, walked.ScorePercent));
        Assert.All(walked.Criteria!, c => Assert.True(c.Passed));

        // Shorter than the baseline (shortcut through the model) is clamped to 1.
        var shortcut = await RouteSession(f.Training,
            LearnerEvent(1, "RouteBaseline", 0, new { optimalDistanceMeters = 40 }),
            LearnerEvent(2, "Moved", 30_000, new { distanceMeters = 30, steps = 38 }));
        Assert.Equal(1m, shortcut.Metrics.GetProperty("route_efficiency").GetDecimal());

        // No baseline and no steps: both criteria are not evaluable (passed null), the mandatory one does not fail, the exit still scores.
        var legacy = await RouteSession(f.Training, LearnerEvent(1, "Moved", 30_000, new { distanceMeters = 45 }));
        Assert.False(legacy.Metrics.TryGetProperty("route_efficiency", out _));
        Assert.False(legacy.Metrics.TryGetProperty("step_count", out _));
        var route = legacy.Criteria!.Single(c => c.Id == "route");
        Assert.Equal(((bool?)null, "METRIC_NOT_EVALUABLE"), (route.Passed, route.Reason));
        Assert.Equal(((bool?)null, "METRIC_NOT_EVALUABLE"), (legacy.Criteria!.Single(c => c.Id == "steps").Passed, legacy.Criteria!.Single(c => c.Id == "steps").Reason));
        Assert.Equal(("Passed", 100m, "ALL_REQUIREMENTS_MET"), (legacy.Outcome, legacy.ScorePercent, legacy.Reason));

        // A baseline but no movement is a route efficiency of 0 (review fix: "did not move" is not "no telemetry").
        var stood = await RouteSession(f.Training, LearnerEvent(1, "RouteBaseline", 0, new { optimalDistanceMeters = 40 }));
        Assert.Equal(0m, stood.Metrics.GetProperty("route_efficiency").GetDecimal());
        Assert.Equal(("NotPassed", "MANDATORY_CRITERION_FAILED"), (stood.Outcome, stood.Reason));

        // A poor route fails the mandatory route criterion.
        var detour = await RouteSession(f.Training,
            LearnerEvent(1, "RouteBaseline", 0, new { optimalDistanceMeters = 40 }),
            LearnerEvent(2, "Moved", 30_000, new { distanceMeters = 100, steps = 130 }));
        Assert.Equal(0.4m, detour.Metrics.GetProperty("route_efficiency").GetDecimal());
        Assert.Equal(("NotPassed", "MANDATORY_CRITERION_FAILED"), (detour.Outcome, detour.Reason));
    }

    [Fact]
    public void Route_events_are_validated_before_storage()
    {
        static TrainingEventsRequest One(string type, object payload) => new([LearnerEvent(1, type, 0, payload)]);
        Assert.NotNull(LearnerSessions.ValidateEvents(One("RouteBaseline", new { })));
        Assert.NotNull(LearnerSessions.ValidateEvents(One("RouteBaseline", new { optimalDistanceMeters = -1 })));
        Assert.NotNull(LearnerSessions.ValidateEvents(One("Moved", new { distanceMeters = 1, steps = 1.5 })));
        Assert.NotNull(LearnerSessions.ValidateEvents(One("Moved", new { distanceMeters = 1, steps = -2 })));
        Assert.Null(LearnerSessions.ValidateEvents(One("Moved", new { distanceMeters = 1, steps = 2 })));
        Assert.Null(LearnerSessions.ValidateEvents(One("Moved", new { distanceMeters = 1 })));
        Assert.Null(LearnerSessions.ValidateEvents(One("RouteBaseline", new { optimalDistanceMeters = 40.5 })));
    }
}
