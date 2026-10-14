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

public sealed partial class AuthIntegrationTests
{
    // Issue #80 decisions plus the corrected completion/duration definitions.
    [PostgresFact]
    public async Task Training_analytics_filters_cohorts_debrief_and_limited_csv_export()
    {
        var f = await PublishedTraining(ScorableRubric);
        var t1 = await SeedTrainee(); var t2 = await SeedTrainee();
        Guid passed = default, learn = default, abandoned = default, waiting = default;
        await WithLearnerRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new LearnerSessions(db, Options.Create(LearnerOptions));
            var a = new LearnerCaller(t1.Id, t1.Family, null); var b = new LearnerCaller(t2.Id, t2.Family, null);
            passed = await Running(store, a, f.Training, "Assessment", "v2-passed");
            await store.RecordEventsAsync(a, passed, new([LearnerEvent(1, "ExitReached", 5000)]), default);
            await store.CompleteAsync(a, passed, new(1, "Finished"), "v2-passed-done", default);
            learn = await Running(store, a, f.Training, "Learn", "v2-learn");
            await store.RecordEventsAsync(a, learn, new([LearnerEvent(1, "ExitReached", 9000)]), default);
            await store.CompleteAsync(a, learn, new(1, "Finished"), "v2-learn-done", default);
            abandoned = await Running(store, b, f.Training, "Assessment", "v2-abandoned");
            await store.CompleteAsync(b, abandoned, new(0, "Abandoned"), "v2-abandoned-done", default);
            waiting = await Running(store, b, f.Training, "Guided", "v2-waiting");
            Assert.Equal(202, (await store.CompleteAsync(b, waiting, new(3, "Finished"), "v2-waiting-done", default)).Value.Status);
        });
        // Monday 5 and Sunday 11 October share one ISO week; the 12th starts the next week.
        await ExecuteAsync($"""
            UPDATE sessions SET started_at='2026-10-05T01:00:00Z' WHERE id='{passed}';UPDATE sessions SET started_at='2026-10-11T23:00:00Z' WHERE id='{learn}';
            UPDATE sessions SET started_at='2026-10-12T00:30:00Z' WHERE id IN('{abandoned}','{waiting}');
            """);
        var analytics = new LearnerAnalytics(BuildingContext(testConnection), Options.Create(new LearnerAnalyticsOptions()));
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var to = DateTime.UtcNow.AddMinutes(1).ToString("O");
        TrainingAnalyticsQuery Q(string? group = null, Guid? building = null, Guid? scenario = null) => new("Organization", null, building, scenario, group, "2026-10-01T00:00:00Z", to);
        async Task<TrainingAnalyticsReport> Read(TrainingAnalyticsQuery query)
        {
            var result = await analytics.ReadTraining(f.Owner, f.OwnerFamily, query, default);
            Assert.True(result.IsSuccess, result.Error?.Code);
            return result.Value.Deserialize<TrainingAnalyticsReport>(web)!;
        }

        var weekly = await Read(Q("week"));
        Assert.Equal((4L, 2L, 2L, 1L, 1L), (weekly.Plays, weekly.UniqueTrainees, weekly.Completed, weekly.Abandoned, weekly.AwaitingSync));
        Assert.Equal(0.5m, weekly.CompletionRate);
        Assert.Equal((2L, 7m, 7m), (weekly.Duration.Sessions, weekly.Duration.MedianSeconds!.Value, weekly.Duration.AverageSeconds!.Value));
        Assert.Equal(new[] { "2026-10-05", "2026-10-12" }, weekly.Cohorts!.Select(x => x.Key));
        Assert.Equal((2L, 1L, 2L), (weekly.Cohorts![0].Plays, weekly.Cohorts[0].UniqueTrainees, weekly.Cohorts[0].Completed));
        Assert.Equal((2L, 1L, 0L, 1L, 1L), (weekly.Cohorts[1].Plays, weekly.Cohorts[1].UniqueTrainees, weekly.Cohorts[1].Completed, weekly.Cohorts[1].Abandoned, weekly.Cohorts[1].AwaitingSync));
        Assert.Null(weekly.ByOrganization);
        Assert.Contains(weekly.Debrief, x => x.CriterionId == "exit" && x.Evaluated == 1 && x.PassRate == 1m);

        var byBuilding = await Read(Q("building"));
        var buildingName = (string)(await ScalarAsync($"SELECT name FROM buildings WHERE id='{f.Building}'"))!;
        Assert.Equal((f.Building.ToString(), buildingName, 2L), (byBuilding.Cohorts!.Single().Key, byBuilding.Cohorts.Single().Label, byBuilding.Cohorts.Single().UniqueTrainees));
        var scenario = (Guid)(await ScalarAsync($"SELECT v.scenario_id FROM sessions s JOIN scenario_versions v ON v.id=s.scenario_version_id WHERE s.id='{passed}'"))!;
        Assert.Equal(4L, (await Read(Q(null, f.Building, scenario))).Plays);
        Assert.Null((await Read(Q())).Cohorts);
        Assert.Equal("NOT_FOUND", (await analytics.ReadTraining(f.Owner, f.OwnerFamily, Q(null, Guid.NewGuid()), default)).Error?.Code);
        Assert.Equal("VALIDATION_ERROR", (await analytics.ReadTraining(f.Owner, f.OwnerFamily, Q("year"), default)).Error?.Code);
        var platform = (await analytics.ReadTraining(adminId, await AdminFamily(), new("Platform", null, null, null, null, "2026-10-01T00:00:00Z", to), default)).Value.Deserialize<TrainingAnalyticsReport>(web)!;
        Assert.NotNull(platform.ByOrganization);

        // Export: same rows as the report, BOM + fixed header, ten per account per UTC day, each audited.
        var export = await analytics.ExportTraining(f.Owner, f.OwnerFamily, Q("week"), default);
        Assert.True(export.IsSuccess, export.Error?.Code);
        Assert.Equal(Encoding.UTF8.GetPreamble(), export.Value!.Content[..3]);
        var lines = Encoding.UTF8.GetString(export.Value.Content[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(string.Join(',', TrainingAnalyticsCsv.Header), lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("week,2026-10-05,2026-10-05,2,1,2,0,0,", lines[1]);
        for (var i = 1; i < 10; i++) Assert.True((await analytics.ExportTraining(f.Owner, f.OwnerFamily, Q("day"), default)).IsSuccess);
        var limited = await analytics.ExportTraining(f.Owner, f.OwnerFamily, Q("day"), default);
        Assert.Equal(("EXPORT_DAILY_LIMIT_EXCEEDED", 429), (limited.Error?.Code, limited.Error?.Status));
        Assert.True(limited.Error!.RetryAfterSeconds > 0);
        Assert.Equal(10L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE user_id='{f.Owner}' AND target_entity='AnalyticsExport'"));
        Assert.Equal(10L, await ScalarAsync($"SELECT count(*) FROM analytics_exports WHERE actor_id='{f.Owner}'"));
    }

    [Fact]
    public void Analytics_csv_neutralises_formulas_and_quotes_separators()
    {
        Assert.Equal("'=SUM(A1:A9)", TrainingAnalyticsCsv.Cell("=SUM(A1:A9)"));
        Assert.Equal("'@cmd", TrainingAnalyticsCsv.Cell("@cmd"));
        Assert.Equal("'+1+1", TrainingAnalyticsCsv.Cell("+1+1"));
        Assert.Equal("-5", TrainingAnalyticsCsv.Cell("-5"));
        Assert.Equal("\"Tòa A, tầng 3\"", TrainingAnalyticsCsv.Cell("Tòa A, tầng 3"));
        Assert.Equal("\"Nói \"\"thoát\"\"\"", TrainingAnalyticsCsv.Cell("Nói \"thoát\""));
        var csv = Encoding.UTF8.GetString(TrainingAnalyticsCsv.Build("building", [new("id", "=Tòa nhà Đông", 1, 1, 1, 0, 0, 0, 0, null, 12.5m)]));
        Assert.Contains("building,id,'=Tòa nhà Đông,1,1,1,0,0,0,0,,12.5", csv);
    }
}
