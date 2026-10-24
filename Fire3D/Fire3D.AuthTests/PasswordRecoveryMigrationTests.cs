using System.Net;
using System.Net.Http.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.ChangePassword;
using Fire3D.Application.Authentication.Commands.ForgotPassword;
using Fire3D.Application.Authentication.Commands.ResetPassword;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // The recovery tables used to exist only through hand-run scripts; the fixture now builds them from migrations alone.
    // With FIRE3D_TEST_RUNTIME_LOGIN=1 the same flow also proves the fire3d_api grants and RLS policies.
    [PostgresFact]
    public async Task Forgot_reset_and_change_password_work_on_a_database_built_only_from_migrations()
    {
        foreach (var table in new[] { "local_password_reset_tokens", "password_reset_email_jobs" })
        {
            Assert.True((bool)(await ScalarAsync($"SELECT relrowsecurity FROM pg_class WHERE oid='public.{table}'::regclass"))!, table);
            Assert.False((bool)(await ScalarAsync($"SELECT has_table_privilege('fire3d_api','public.{table}','DELETE')"))!, table);
        }
        // The Firebase-era reset tables and the other tables nothing read any more are dropped by DropUnusedTables.
        foreach (var table in new[] { "password_reset_tokens", "password_reset_operations", "release_qr_codes", "revision_floors",
            "building_floors", "session_checkpoints", "debrief_artifacts", "invoice_metadata" })
            Assert.Equal(true, await ScalarAsync($"SELECT to_regclass('public.{table}') IS NULL"));
        Assert.Equal(true, await ScalarAsync("SELECT to_regprocedure('public.invalidate_legacy_reset_tokens(uuid)') IS NULL"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM information_schema.columns WHERE table_name='sessions' AND column_name='qr_code_id'"));

        var stale = await LoginAsync();
        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordCommand("admin@example.test"))).StatusCode);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM password_reset_email_jobs WHERE email='admin@example.test'"));

        // The email worker is disabled in tests; create the link through the same runtime identity the worker uses.
        string link;
        using (var scope = factory!.Services.CreateScope())
            link = await scope.ServiceProvider.GetRequiredService<ILocalPasswordReset>().CreateLinkAsync("admin@example.test", default)
                ?? throw new InvalidOperationException("Eligible admin did not receive a reset link.");
        var token = link[(link.IndexOf("token=", StringComparison.Ordinal) + 6)..];
        const string resetPassword = "reset-pass-1";
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordCommand(token, resetPassword))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/auth/reset-password", new ResetPasswordCommand(token, "reset-pass-2"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(stale.RefreshToken))).StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.test", resetPassword));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = (await login.Content.ReadFromJsonAsync<LoginResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", session.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(resetPassword, "changed-pass-1"))).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.test", "changed-pass-1"))).StatusCode);
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM local_password_reset_tokens WHERE used_at IS NULL"));
    }
}
