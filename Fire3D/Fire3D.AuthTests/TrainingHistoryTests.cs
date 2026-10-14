using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Learning;
using Fire3D.Infrastructure.Learning;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // Issue #78: a Trainee lists only their own started sessions with session status and stored result kept separate.
    [PostgresFact]
    public async Task Training_history_lists_own_sessions_with_results_filters_and_paging()
    {
        var f = await PublishedTraining(ScorableRubric);
        var trainee = await SeedTrainee(); var other = await SeedTrainee();
        await WithLearnerRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new LearnerSessions(db, Options.Create(LearnerOptions));
            var caller = new LearnerCaller(trainee.Id, trainee.Family, null);
            var learn = await Running(store, caller, f.Training, "Learn", "history-learn");
            await store.RecordEventsAsync(caller, learn, new([LearnerEvent(1, "ExitReached", 5000)]), default);
            await store.CompleteAsync(caller, learn, new(1, "Finished"), "history-learn-complete", default);
            var abandoned = await Running(store, caller, f.Training, "Assessment", "history-assessment");
            await store.CompleteAsync(caller, abandoned, new(0, "Abandoned"), "history-assessment-complete", default);
            var running = await Running(store, caller, f.Training, "Guided", "history-running");
            // A preparation that was never started is not a play and must not be listed.
            Assert.True((await store.PrepareAsync(caller, new(f.Training, "Learn", "1.0.0"), "history-prepared-only", default)).IsSuccess);
            var otherCaller = new LearnerCaller(other.Id, other.Family, null);
            var foreign = await Running(store, otherCaller, f.Training, "Learn", "history-other");
            await ExecuteAsync($"UPDATE sessions SET started_at=now()-interval '3 minutes' WHERE id='{learn}';UPDATE sessions SET started_at=now()-interval '2 minutes' WHERE id='{abandoned}';UPDATE sessions SET started_at=now()-interval '1 minute' WHERE id='{running}'");

            async Task<TrainingHistoryPage> List(TrainingHistoryFilter filter, LearnerCaller? who = null)
            {
                var result = await store.ListAsync(who ?? caller, filter, default);
                Assert.True(result.IsSuccess, result.Error?.Code);
                return result.Value!;
            }
            var all = await List(new(null, null, null, null, null));
            Assert.Equal(3, all.TotalCount);
            Assert.Equal(new[] { running, abandoned, learn }, all.Items.Select(x => x.SessionId));
            Assert.DoesNotContain(all.Items, x => x.SessionId == foreign);
            var runningItem = all.Items[0];
            Assert.Equal("Running", runningItem.Status); Assert.Null(runningItem.ResultStatus); Assert.Equal("Guided", runningItem.Mode);
            Assert.Equal(("Completed", "Incomplete", "Abandoned"), (all.Items[1].Status, all.Items[1].ResultStatus, all.Items[1].EndReason));
            Assert.Equal(("Completed", "NotAssessed"), (all.Items[2].Status, all.Items[2].ResultStatus));
            Assert.Equal(f.Building, runningItem.Building.Id); Assert.False(string.IsNullOrEmpty(runningItem.Building.Name));
            Assert.Equal(f.Training, runningItem.Training.Id); Assert.False(string.IsNullOrEmpty(runningItem.Scenario.Name));

            Assert.Equal(new[] { abandoned }, (await List(new(null, "assessment", null, null, null))).Items.Select(x => x.SessionId));
            Assert.Equal(new[] { running }, (await List(new("Running", null, null, null, null))).Items.Select(x => x.SessionId));
            Assert.Equal(2, (await List(new("Completed", null, null, null, null))).TotalCount);
            Assert.Equal(0, (await List(new(null, null, Guid.NewGuid(), null, null))).TotalCount);
            Assert.Equal(0, (await List(new(null, null, null, DateTimeOffset.UtcNow.AddDays(1), null))).TotalCount);
            Assert.Equal(new[] { abandoned, learn }, (await List(new(null, null, null, null, DateTimeOffset.UtcNow.AddSeconds(-90)))).Items.Select(x => x.SessionId));
            var paged = await List(new(null, null, null, null, null, Page: 2, PageSize: 1));
            Assert.Equal(3, paged.TotalCount); Assert.Equal(new[] { abandoned }, paged.Items.Select(x => x.SessionId));
            Assert.Equal(new[] { foreign }, (await List(new(null, null, null, null, null), otherCaller)).Items.Select(x => x.SessionId));

            // Losing Building access keeps the Trainee's own history and results readable.
            await ExecuteAsync($"UPDATE buildings SET visibility='Private' WHERE id='{f.Building}'");
            Assert.Equal(3, (await List(new(null, null, null, null, null))).TotalCount);
            Assert.True((await store.ResultAsync(caller, learn, default)).IsSuccess);

            var invalid = await store.ListAsync(caller, new("Done", "Exam", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-1), 0, 101), default);
            Assert.Equal("VALIDATION_ERROR", invalid.Error?.Code);
            Assert.Equal(new[] { "from", "mode", "page", "pageSize", "status" }, invalid.Error!.Errors!.Keys.Order());
            Assert.Equal("FORBIDDEN", (await store.ListAsync(new(f.Owner, f.OwnerFamily, null), new(null, null, null, null, null), default)).Error?.Code);
            await ExecuteAsync($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE family_id='{trainee.Family}'");
            Assert.Equal("UNAUTHORIZED", (await store.ListAsync(caller, new(null, null, null, null, null), default)).Error?.Code);
        });

        // HTTP: the route is Trainee-only and validates filters before reaching the database.
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/training/sessions")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/training/sessions")).StatusCode);
    }
}
