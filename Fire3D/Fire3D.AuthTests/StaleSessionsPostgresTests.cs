using System.Text.Json;
using Fire3D.Application.Learning;
using Fire3D.Infrastructure.Learning;
using Fire3D.Infrastructure.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // Moves every clock of one session (start, launch grant, launch, heartbeat, received events) into the past.
    private Task AgeSession(Guid session, string by) => ExecuteAsync($"""
        SET session_replication_role=replica;
        UPDATE sessions SET started_at=started_at-interval '{by}',grant_issued_at=grant_issued_at-interval '{by}',grant_expires_at=grant_expires_at-interval '{by}',
         launched_at=launched_at-interval '{by}',last_heartbeat_at=last_heartbeat_at-interval '{by}' WHERE id='{session}';
        UPDATE session_events SET received_at=received_at-interval '{by}' WHERE session_id='{session}';
        SET session_replication_role=origin;
        """);

    private async Task<int> CloseStaleSessions()
    {
        var worker = new StaleLearnerSessionWorker(WorkerHarness.Scopes(s => s.AddScoped(_ => BuildingContext(testConnection))),
            Options.Create(new LearnerSessionOptions()), new CapturedLog<StaleLearnerSessionWorker>());
        return await worker.RunOnceAsync(default);
    }

    private async Task<string> SessionStatus(Guid session) => (string)(await ScalarAsync($"SELECT status::text FROM sessions WHERE id='{session}'"))!;

    // #207: a play the phone never completes ends as Abandoned at its acknowledged sequence once it is silent and can no longer sync;
    // a live play, a play that may still sync offline, and a play whose completion is waiting for events are left alone.
    [PostgresFact]
    public async Task Stale_learner_sessions_close_as_abandoned_but_live_or_syncable_ones_stay()
    {
        var f = await PublishedTraining(ScorableRubric);
        var trainee = await SeedTrainee();
        await WithLearnerRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new LearnerSessions(db, Options.Create(LearnerOptions));
            var caller = new LearnerCaller(trainee.Id, trainee.Family, null);
            var silent = await Running(store, caller, f.Training, "Assessment", "silent");
            Assert.True((await store.RecordEventsAsync(caller, silent, new([LearnerEvent(1, "Moved", 1000, new { distanceMeters = 2.5 }), LearnerEvent(3, "ExitReached", 5000)]), default)).IsSuccess);
            var offline = await Running(store, caller, f.Training, "Assessment", "offline");
            var live = await Running(store, caller, f.Training, "Assessment", "live");
            var completing = await Running(store, caller, f.Training, "Assessment", "completing");
            Assert.Equal(202, (await store.CompleteAsync(caller, completing, new(2, "Finished"), "completing-complete", default)).Value.Status);
            var prepared = await store.PrepareAsync(caller, new(f.Training, "Learn", "1.0.0"), "never-launched-prepare", default);
            Assert.True((await store.StartAsync(caller, prepared.Value!.Id, "never-launched-start", default)).IsSuccess);
            var launching = prepared.Value.Id;

            await AgeSession(silent, "2 days");
            await AgeSession(launching, "2 days");
            await AgeSession(completing, "2 days");
            // Silent for 2 hours, but its continuation still lets the phone sync an offline play.
            await AgeSession(offline, "2 hours");

            Assert.True(await CloseStaleSessions() >= 2);
            Assert.Equal("Completed", await SessionStatus(silent));
            Assert.Equal("Completed", await SessionStatus(launching));
            Assert.Equal("Running", await SessionStatus(offline));
            Assert.Equal("Running", await SessionStatus(live));
            Assert.Equal("Running", await SessionStatus(completing));

            // The result is the normal immutable one: Incomplete for an Assessment, at the acknowledged sequence (event 2 never came).
            var result = (await store.ResultAsync(caller, silent, default)).Value!;
            Assert.Equal("Incomplete", result.Outcome);
            Assert.Equal("ENDED_BEFORE_COMPLETION", result.Reason);
            Assert.Equal(1, result.LastEventSequence);
            Assert.Equal("Abandoned", (string?)await ScalarAsync($"SELECT completion_end_reason FROM sessions WHERE id='{silent}'"));
            Assert.Equal("NotAssessed", (string?)await ScalarAsync($"SELECT outcome FROM session_results WHERE session_id='{launching}'"));
            Assert.Equal(2L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_entity='Session' AND new_values->>'operation'='CloseStale' AND target_id IN('{silent}','{launching}')"));
            // A late upload from that phone meets a finished session; nothing is rewritten.
            Assert.Equal("SESSION_TERMINAL", (await store.RecordEventsAsync(caller, silent, new([LearnerEvent(2, "Moved", 2000, new { distanceMeters = 1.0 })]), default)).Error?.Code);

            // Once the continuation has expired the offline play cannot sync any more and is closed too; a second run changes nothing else.
            await ExecuteAsync($"UPDATE sessions SET continuation_expires_at=now()-interval '1 minute' WHERE id='{offline}'");
            await CloseStaleSessions();
            Assert.Equal("Completed", await SessionStatus(offline));
            Assert.Equal("Running", await SessionStatus(live));
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM session_results WHERE session_id='{silent}'"));
        });
        if ((long)(await ScalarAsync("SELECT count(*) FROM pg_roles WHERE rolname='fet3d_backend_executor'"))! == 1)
            Assert.True((bool)(await ScalarAsync("SELECT has_function_privilege('fet3d_backend_executor','learner_session_close_stale(integer)','EXECUTE')"))!);
        Assert.False((bool)(await ScalarAsync("SELECT has_function_privilege('public','learner_session_close_stale(integer)','EXECUTE')"))!);
    }

    // #208: only an Assessment in progress blocks the Trainee's AI; a stale row left by an earlier attempt does not.
    [PostgresFact]
    public async Task Ai_block_counts_only_an_assessment_in_progress()
    {
        var trainee = await SeedTrainee();
        var org = Guid.NewGuid(); var training = Guid.NewGuid();
        async Task<bool> Blocked()
        {
            using var quota = JsonDocument.Parse((string)(await ScalarAsync($"SELECT ai_trainee_gate('Quota','{trainee.Id}','{trainee.Family}',NULL,'{{}}',NULL)::text"))!);
            return quota.RootElement.GetProperty("result").GetProperty("blockedByAssessment").GetBoolean();
        }
        async Task<Guid> Latest() => (Guid)(await ScalarAsync($"SELECT id FROM sessions WHERE trainee_user_id='{trainee.Id}' ORDER BY started_at DESC LIMIT 1"))!;
        Task End(Guid session) => ExecuteAsync($"SET session_replication_role=replica;UPDATE sessions SET status='Completed' WHERE id='{session}';SET session_replication_role=origin;");

        Assert.False(await Blocked());
        // Stuck rows from earlier attempts: silent Running, Launching whose grant expired, a Learn play.
        await SeedAnalyticsSession(org, trainee.Id, training, "now()-interval '3 days'", status: "Running", mode: "Assessment", launchedAt: "now()-interval '3 days'", heartbeatAt: "now()-interval '10 minutes'");
        await SeedAnalyticsSession(org, trainee.Id, training, "now()-interval '1 hour'", status: "Launching", mode: "Assessment");
        await SeedAnalyticsSession(org, trainee.Id, training, "now()", status: "Running", mode: "Learn", heartbeatAt: "now()");
        Assert.False(await Blocked());

        // A launch whose grant is still valid blocks.
        await SeedAnalyticsSession(org, trainee.Id, training, "now()", status: "Launching", mode: "Assessment");
        Assert.True(await Blocked());
        await End(await Latest());

        // A play heartbeating now blocks; once its completion is requested the Trainee is done with it.
        await SeedAnalyticsSession(org, trainee.Id, training, "now()+interval '1 second'", status: "Running", mode: "Assessment", launchedAt: "now()", heartbeatAt: "now()-interval '1 minute'");
        Assert.True(await Blocked());
        await ExecuteAsync($"SET session_replication_role=replica;UPDATE sessions SET completion_key='complete',completion_last_sequence=3,completion_end_reason='Finished',completion_requested_at=now() WHERE id='{await Latest()}';SET session_replication_role=origin;");
        Assert.False(await Blocked());
    }
}
