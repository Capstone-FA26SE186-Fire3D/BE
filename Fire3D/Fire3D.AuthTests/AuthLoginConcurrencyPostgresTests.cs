using System.Net;
using System.Net.Http.Json;
using Fire3D.Application.Authentication;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Login_waiting_for_lock_rejects_password_changed_by_another_transaction()
        => await LoginRace("UPDATE users SET password_hash='replaced-by-reset'",HttpStatusCode.Unauthorized);

    [PostgresFact]
    public async Task Login_waiting_for_lock_rechecks_disabled_account()
        => await LoginRace("UPDATE users SET is_active=false",HttpStatusCode.Forbidden);

    [PostgresFact]
    public async Task Login_waiting_for_lock_cannot_bypass_a_pending_password_reset()
        => await LoginRace($"INSERT INTO password_reset_operations(id,user_id,firebase_uid,code_hash,status) VALUES(gen_random_uuid(),'{adminId}','test',repeat('a',64),'Pending')",HttpStatusCode.ServiceUnavailable);

    private async Task LoginRace(string mutation,HttpStatusCode expected)
    {
        await using var writer=new NpgsqlConnection(testConnection);await writer.OpenAsync();
        await using var transaction=await writer.BeginTransactionAsync();
        await new NpgsqlCommand($"SELECT pg_advisory_xact_lock(hashtextextended('fire3d:auth:{adminId}',0))",writer,transaction).ExecuteNonQueryAsync();
        var login=client.PostAsJsonAsync("/api/auth/login",new LoginRequest("admin@example.test",password));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while(!Equals(await ScalarAsync("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory')"),true))
        {
            if(login.IsCompleted)Assert.Fail("Login did not wait for the identity lock.");
            await Task.Delay(20,timeout.Token);
        }
        await new NpgsqlCommand(mutation,writer,transaction).ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        Assert.Equal(expected,(await login.WaitAsync(TimeSpan.FromSeconds(15))).StatusCode);
        Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM auth_refresh_tokens"));
        Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM audit_logs WHERE action='Login'"));
    }

    [PostgresFact]
    public async Task Login_audit_failure_rolls_back_session_and_last_login()
    {
        await ExecuteAsync("""
            CREATE FUNCTION fail_login_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
              IF NEW.action='Login' THEN RAISE EXCEPTION 'test login audit failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_login_audit BEFORE INSERT ON audit_logs FOR EACH ROW EXECUTE FUNCTION fail_login_audit();
            """);
        Assert.Equal(HttpStatusCode.InternalServerError,(await client.PostAsJsonAsync("/api/auth/login",new LoginRequest("admin@example.test",password))).StatusCode);
        Assert.Equal(0L,await ScalarAsync("SELECT count(*) FROM auth_refresh_tokens"));
        Assert.Equal(true,await ScalarAsync($"SELECT last_login_at IS NULL FROM users WHERE id='{adminId}'"));
    }
}
