using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fire3D.Application.Learning;
using Fire3D.Application.Reporting;
using Fire3D.Infrastructure.Learning;
using Fire3D.Infrastructure.Reporting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

// BE#218 (FR-PLAN-11): training log per situation — a year-long range for groupBy=scenario, lastPlayedAt, audience/workingHours,
// published situations without a play listed with plays 0, and the CSV disclaimer line.
public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Training_log_by_scenario_spans_a_year_and_lists_unplayed_published_situations()
    {
        var played = await PublishedTraining(ScorableRubric);
        var trainee = await SeedTrainee();
        Guid session = default;
        await WithLearnerRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new LearnerSessions(db, Options.Create(LearnerOptions));
            var caller = new LearnerCaller(trainee.Id, trainee.Family, null);
            session = await Running(store, caller, played.Training, "Assessment", "log-played");
            await store.RecordEventsAsync(caller, session, new([LearnerEvent(1, "ExitReached", 5000)]), default);
            await store.CompleteAsync(caller, session, new(1, "Finished"), "log-played-done", default);
        });
        await ExecuteAsync($"UPDATE sessions SET started_at='2026-03-02T08:00:00Z' WHERE id='{session}'");
        var playedScenario = (Guid)(await ScalarAsync($"SELECT v.scenario_id FROM sessions s JOIN scenario_versions v ON v.id=s.scenario_version_id WHERE s.id='{session}'"))!;
        // A second published situation of the same organization, never played; its newest version names audience and hours (BE#215).
        var org = (Guid)(await ScalarAsync($"SELECT organization_id FROM trainings WHERE id='{played.Training}'"))!;
        var unplayedVersion = Guid.NewGuid(); var unplayedScenario = Guid.NewGuid(); var release = Guid.NewGuid();
        await ExecuteAsync($$"""
            INSERT INTO scenarios(id,building_id,organization_id,name,created_by,created_at) SELECT '{{unplayedScenario}}',building_id,organization_id,'Ngoài giờ - tầng 3',created_by,now() FROM scenarios WHERE id='{{playedScenario}}';
            INSERT INTO scenario_versions(id,scenario_id,revision_id,building_id,organization_id,version_number,name,schema_version,algorithm_version,random_seed,time_limit_seconds,spawn_config,goal_config,fire_source_config,npc_config,blocked_elements,routing_config,scoring_config,mode_policy,safety_thresholds,replan_interval_seconds,scenario_hash,created_by,created_at,state_snapshot,learning_objectives,learner_instructions,rubric)
            SELECT '{{unplayedVersion}}','{{unplayedScenario}}',revision_id,building_id,organization_id,1,'Ngoài giờ - tầng 3',schema_version,algorithm_version,random_seed,time_limit_seconds,spawn_config,goal_config,fire_source_config,npc_config,blocked_elements,routing_config,scoring_config,mode_policy,safety_thresholds,replan_interval_seconds,
             fet3d_jsonb_payload_hash(state_snapshot||'{"audience":"EvacuationGuide","workingHours":"OffHours"}'),created_by,now(),state_snapshot||'{"audience":"EvacuationGuide","workingHours":"OffHours"}',learning_objectives,learner_instructions,rubric
            FROM scenario_versions WHERE scenario_id='{{playedScenario}}' ORDER BY version_number DESC LIMIT 1;
            """);
        await ExecuteAsync($"""
            ALTER TABLE releases DISABLE TRIGGER USER;ALTER TABLE trainings DISABLE TRIGGER USER;
            INSERT INTO releases SELECT (jsonb_populate_record(r,jsonb_build_object('id','{release}','scenario_version_id','{unplayedVersion}'))).* FROM releases r WHERE r.id=(SELECT release_id FROM trainings WHERE id='{played.Training}');
            INSERT INTO trainings SELECT (jsonb_populate_record(t,jsonb_build_object('id',gen_random_uuid(),'release_id','{release}','scenario_version_id','{unplayedVersion}','name','Ngoài giờ - tầng 3'))).* FROM trainings t WHERE t.id='{played.Training}';
            ALTER TABLE releases ENABLE TRIGGER USER;ALTER TABLE trainings ENABLE TRIGGER USER;
            """);

        var analytics = new LearnerAnalytics(BuildingContext(testConnection), Options.Create(new LearnerAnalyticsOptions()));
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        const string from = "2025-12-01T00:00:00Z", to = "2026-12-01T00:00:00Z"; // 365 days
        var year = await analytics.ReadTraining(played.Owner, played.OwnerFamily, new("Organization", null, null, null, "scenario", from, to), default);
        Assert.True(year.IsSuccess, year.Error?.Code);
        var cohorts = year.Value.Deserialize<TrainingAnalyticsReport>(web)!.Cohorts!;
        var playedRow = cohorts.Single(c => c.Key == playedScenario.ToString());
        Assert.Equal(1L, playedRow.Plays);
        Assert.Equal(DateTimeOffset.Parse("2026-03-02T08:00:00Z"), playedRow.LastPlayedAt);
        var unplayed = cohorts.Single(c => c.Key == unplayedScenario.ToString());
        Assert.Equal((0L, (DateTimeOffset?)null, "EvacuationGuide", "OffHours", "Ngoài giờ - tầng 3"), (unplayed.Plays, unplayed.LastPlayedAt, unplayed.Audience, unplayed.WorkingHours, unplayed.Label));

        // Without from/to the training log covers the last 365 days, so a play 9 months ago still counts (review fix).
        var byDefault = (await analytics.ReadTraining(played.Owner, played.OwnerFamily, new("Organization", null, null, null, "scenario", null, null), default)).Value
            .Deserialize<TrainingAnalyticsReport>(web)!.Cohorts!;
        Assert.Equal(1L, byDefault.Single(c => c.Key == playedScenario.ToString()).Plays);
        // A training past its end date is no longer a situation to practise (review fix).
        await ExecuteAsync($"UPDATE trainings SET end_date=now()-interval '1 day' WHERE release_id='{release}'");
        var afterEnd = (await analytics.ReadTraining(played.Owner, played.OwnerFamily, new("Organization", null, null, null, "scenario", from, to), default)).Value
            .Deserialize<TrainingAnalyticsReport>(web)!.Cohorts!;
        Assert.DoesNotContain(afterEnd, c => c.Key == unplayedScenario.ToString());
        await ExecuteAsync($"UPDATE trainings SET end_date=NULL WHERE release_id='{release}'");

        // Every other grouping keeps the 90-day limit; a year by scenario is fine, more than 366 days is not.
        Assert.Equal("VALIDATION_ERROR", (await analytics.ReadTraining(played.Owner, played.OwnerFamily, new("Organization", null, null, null, "day", from, to), default)).Error?.Code);
        Assert.Equal("VALIDATION_ERROR", (await analytics.ReadTraining(played.Owner, played.OwnerFamily, new("Organization", null, null, null, "scenario", "2025-11-01T00:00:00Z", to), default)).Error?.Code);

        // CSV: the disclaimer line first, then the header with the appended columns.
        var export = await analytics.ExportTraining(played.Owner, played.OwnerFamily, new("Organization", null, null, null, "scenario", from, to), default);
        Assert.True(export.IsSuccess, export.Error?.Code);
        var lines = Encoding.UTF8.GetString(export.Value!.Content[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(TrainingAnalyticsCsv.Cell(TrainingAnalyticsCsv.Disclaimer), lines[0]);
        Assert.StartsWith("groupBy,key,label,plays,uniqueTrainees,completed,abandoned,awaitingSync,assessed,passed,passRate,averageDurationSeconds,lastPlayedAt,audience,workingHours", lines[1]);
        Assert.Contains(lines, l => l.StartsWith($"scenario,{unplayedScenario},") && l.EndsWith(",,EvacuationGuide,OffHours"));
    }
}
