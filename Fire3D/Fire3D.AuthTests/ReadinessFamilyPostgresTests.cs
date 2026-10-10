using Fire3D.Application.Scenarios;
using Fire3D.Infrastructure.Scenarios;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    private string RuntimeTestPassword => (new NpgsqlConnectionStringBuilder(testConnection).Password ?? "").Replace("'", "''");
    [PostgresFact]
    public async Task Readiness_rechecks_family_after_waiting_and_before_receipt_replay()
    {
        var fixture = await SeedReadyPackage();
        await WithReadinessRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime); var store = new ScenarioReadinessStore(db);
            var input = new ConfirmTrainingRequest(fixture.Version,fixture.Run);
            var first = await store.ExecuteAsync("Confirm",fixture.Owner,fixture.Owner,fixture.Version,fixture.Revision,input,null,default);
            Assert.True(first.IsSuccess,first.Error?.Code);
            var before = await ScalarAsync("SELECT count(*) FROM readiness_command_receipts");
            await using var held = new NpgsqlConnection(testConnection); await held.OpenAsync();
            await using var tx = await held.BeginTransactionAsync();
            await new NpgsqlCommand($"SELECT pg_advisory_xact_lock(hashtextextended('fire3d:auth:{fixture.Owner}',0))",held,tx).ExecuteNonQueryAsync();
            var replay = store.ExecuteAsync("Confirm",fixture.Owner,fixture.Owner,fixture.Version,fixture.Revision,input,null,default);
            var waiting = false;
            for (var i=0;i<200;i++)
            {
                waiting = (bool)(await ScalarAsync("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory')"))!;
                if (waiting) break;
                await Task.Delay(25);
            }
            Assert.True(waiting);
            await new NpgsqlCommand($"UPDATE auth_refresh_tokens SET revoked_at=now() WHERE user_id='{fixture.Owner}'",held,tx).ExecuteNonQueryAsync();
            await tx.CommitAsync();
            Assert.Equal(401,(await replay).Error?.Status);
            Assert.Equal(before,await ScalarAsync("SELECT count(*) FROM readiness_command_receipts"));
        });
    }

    [PostgresFact]
    public async Task All_readiness_actions_reject_revoked_and_missing_families_and_legacy_gate_fails_closed()
    {
        await using var db = BuildingContext(testConnection); var store = new ScenarioReadinessStore(db);
        foreach (var action in new[]{"Confirm","TechnicalReject","Submit","Approve","Reject"})
            Assert.Equal(401,(await store.ExecuteAsync(action,adminId,Guid.NewGuid(),Guid.NewGuid(),null,new{},"family-test",default)).Error?.Status);
        Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM readiness_command_receipts"));
        var legacy = (string)(await ScalarAsync($"SELECT scenario_readiness_gate('Submit','{adminId}','{Guid.NewGuid()}',NULL,'{{}}','legacy')::text"))!;
        Assert.Contains("UNAUTHORIZED",legacy);
    }
}
