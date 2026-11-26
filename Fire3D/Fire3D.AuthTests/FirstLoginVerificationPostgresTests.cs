using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Fire3D.AuthTests;

// BE#235: a FireSafetyExpert account created by PlatformAdmin proves its email at the first sign-in (code or Google of that email, always
// after the password), then must change the initial password. PlatformAdmin fixes a mistyped email; nobody can take the account by email alone.
public sealed partial class AuthIntegrationTests
{
    private sealed class FixedGoogle(Dictionary<string, VerifiedIdentity> identities) : IIdentityProvider
    {
        public Task<VerifiedIdentity> VerifyGoogleTokenAsync(string idToken, CancellationToken ct) =>
            identities.TryGetValue(idToken, out var identity) ? Task.FromResult(identity) : throw new GoogleIdentityException(GoogleIdentityFailure.InvalidToken);
        public Task<IdentityUser> CreateEmailPasswordUserAsync(string email, string password, string fullName, CancellationToken ct) => throw new NotSupportedException();
        public Task<VerifiedIdentity> SignInWithPasswordAsync(string email, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteUserAsync(string uid, CancellationToken ct) => throw new NotSupportedException();
    }

    private async Task<(Guid Id, string Email)> CreateExpertAsync(string? email = null)
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        email ??= $"expert-{Guid.NewGuid():N}@example.test";
        var created = await client.PostAsJsonAsync("/api/accounts", new { email, password, fullName = "Chuyên viên PCCC", role = "FireSafetyExpert" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        return ((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid(), email);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostJson(HttpClient http, string url, object body)
    {
        var response = await http.PostAsJsonAsync(url, body);
        return (response.StatusCode, response.Content.Headers.ContentLength == 0 ? default : await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private async Task<string> ChallengeAsync(string email, string secret, HttpClient? http = null)
    {
        var (status, body) = await PostJson(http ?? client, "/api/auth/login", new { email, password = secret });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("FIRST_LOGIN_VERIFICATION_REQUIRED", body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("accessToken", out _));
        return body.GetProperty("challengeToken").GetString()!;
    }

    private async Task<FirstLoginEmailJob?> SentCodeAsync()
    {
        using var scope = factory!.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IFirstLoginDeliveryQueue>().ClaimAsync(default);
    }

    private static string Wrong(string code) => code == "000000" ? "111111" : "000000";

    [PostgresFact]
    public async Task Expert_first_sign_in_needs_the_emailed_code_then_a_password_change()
    {
        var (id, email) = await CreateExpertAsync();
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var listed = (await client.GetFromJsonAsync<JsonElement>("/api/accounts?search=" + Uri.EscapeDataString(email))).GetProperty("items")[0];
        Assert.Equal(("VerifyEmail", JsonValueKind.Null), (listed.GetProperty("firstLoginStep").GetString(), listed.GetProperty("emailVerifiedAt").ValueKind));
        client.DefaultRequestHeaders.Authorization = null;

        // The correct password opens no session: a code goes to the email.
        var (status, body) = await PostJson(client, "/api/auth/login", new { email, password });
        Assert.Equal((HttpStatusCode.Forbidden, "FIRST_LOGIN_VERIFICATION_REQUIRED"), (status, body.GetProperty("code").GetString()));
        Assert.Equal($"{email[0]}***@example.test", body.GetProperty("emailHint").GetString());
        Assert.Equal(60, body.GetProperty("resendAfterSeconds").GetInt32());
        var first = body.GetProperty("challengeToken").GetString()!;
        var job = await SentCodeAsync();
        Assert.Equal(email, job!.Email);
        Assert.Matches("^[0-9]{6}$", job.Code);
        Assert.Equal((HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS"), ((await PostJson(client, "/api/auth/login", new { email, password = "Wrong-" + password })).Status, "INVALID_CREDENTIALS"));

        // A second sign-in reuses the live code (no new email) and replaces the challenge token.
        var second = await ChallengeAsync(email, password);
        Assert.Null(await SentCodeAsync());
        Assert.Equal("FIRST_LOGIN_CHALLENGE_INVALID", (await PostJson(client, "/api/auth/first-login/verify", new { challengeToken = first, code = job.Code })).Body.GetProperty("code").GetString());
        var wrong = await PostJson(client, "/api/auth/first-login/verify", new { challengeToken = second, code = Wrong(job.Code) });
        Assert.Equal((HttpStatusCode.BadRequest, "FIRST_LOGIN_CODE_INVALID", 4), (wrong.Status, wrong.Body.GetProperty("code").GetString(), wrong.Body.GetProperty("attemptsRemaining").GetInt32()));

        var verified = await PostJson(client, "/api/auth/first-login/verify", new { challengeToken = second, code = job.Code });
        Assert.Equal(HttpStatusCode.OK, verified.Status);
        Assert.Equal("ChangePassword", verified.Body.GetProperty("user").GetProperty("firstLoginStep").GetString());
        Assert.NotEqual(DBNull.Value, await ScalarAsync($"SELECT email_verified_at FROM users WHERE id='{id}'"));
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM first_login_verifications WHERE user_id='{id}'"));

        // Until the password changes, only change-password, /api/auth/me and logout are open.
        using var expert = factory!.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        expert.DefaultRequestHeaders.Authorization = new("Bearer", verified.Body.GetProperty("accessToken").GetString());
        var blocked = await expert.GetAsync("/api/expert/reviews");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("PASSWORD_CHANGE_REQUIRED", (await blocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await expert.GetAsync("/api/auth/me")).StatusCode);
        var changed = await expert.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = password, newPassword = "Expert-new-1" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(DBNull.Value, await ScalarAsync($"SELECT first_login_step FROM users WHERE id='{id}'"));
        // Changing the password revoked that session; the next sign-in is a normal one.
        Assert.Equal(HttpStatusCode.Unauthorized, (await expert.GetAsync("/api/auth/me")).StatusCode);
        var normal = await PostJson(client, "/api/auth/login", new { email, password = "Expert-new-1" });
        Assert.Equal(HttpStatusCode.OK, normal.Status);
        Assert.Equal(JsonValueKind.Null, normal.Body.GetProperty("user").GetProperty("firstLoginStep").ValueKind);
        expert.DefaultRequestHeaders.Authorization = new("Bearer", normal.Body.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await expert.GetAsync("/api/expert/reviews")).StatusCode);
    }

    [PostgresFact]
    public async Task First_sign_in_codes_lock_after_five_wrong_tries_expire_and_resending_is_rate_limited()
    {
        var (id, email) = await CreateExpertAsync();
        var token = await ChallengeAsync(email, password);
        var code = (await SentCodeAsync())!.Code;
        for (var i = 1; i <= 4; i++)
            Assert.Equal(5 - i, (await PostJson(client, "/api/auth/first-login/verify", new { challengeToken = token, code = Wrong(code) })).Body.GetProperty("attemptsRemaining").GetInt32());
        Assert.Equal("FIRST_LOGIN_CODE_LOCKED", (await PostJson(client, "/api/auth/first-login/verify", new { challengeToken = token, code = Wrong(code) })).Body.GetProperty("code").GetString());
        Assert.Equal("FIRST_LOGIN_CODE_LOCKED", (await PostJson(client, "/api/auth/first-login/verify", new { challengeToken = token, code })).Body.GetProperty("code").GetString());
        Assert.Equal("FIRST_LOGIN_CODE_INVALID", (await PostJson(client, "/api/auth/first-login/verify", new { challengeToken = token, code = "12ab56" })).Body.GetProperty("code").GetString());

        // A new code at most once a minute.
        var early = await client.PostAsJsonAsync("/api/auth/first-login/resend", new { challengeToken = token });
        Assert.Equal(HttpStatusCode.TooManyRequests, early.StatusCode);
        Assert.Equal("OTP_RATE_LIMITED", (await early.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.NotNull(early.Headers.RetryAfter);
        await ExecuteAsync($"UPDATE first_login_verifications SET last_sent_at=now()-interval '2 minutes' WHERE user_id='{id}'");
        var resent = await PostJson(client, "/api/auth/first-login/resend", new { challengeToken = token });
        Assert.Equal(HttpStatusCode.Accepted, resent.Status);
        Assert.Equal(token, resent.Body.GetProperty("challengeToken").GetString());
        var newCode = (await SentCodeAsync())!.Code;

        // A code expires after 10 minutes.
        await ExecuteAsync($"UPDATE first_login_verifications SET otp_expires_at=now()-interval '1 second' WHERE user_id='{id}'");
        Assert.Equal("FIRST_LOGIN_CODE_EXPIRED", (await PostJson(client, "/api/auth/first-login/verify", new { challengeToken = token, code = newCode })).Body.GetProperty("code").GetString());
        // Five codes an hour.
        await ExecuteAsync($"UPDATE first_login_verifications SET last_sent_at=now()-interval '2 minutes',sends_in_window=5 WHERE user_id='{id}'");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/auth/first-login/resend", new { challengeToken = token })).StatusCode);
        await ExecuteAsync($"UPDATE first_login_verifications SET send_window_started_at=now()-interval '2 hours' WHERE user_id='{id}'");
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/auth/first-login/resend", new { challengeToken = token })).StatusCode);
        var lastCode = (await SentCodeAsync())!.Code;
        // An expired challenge needs a new password sign-in.
        await ExecuteAsync($"UPDATE first_login_verifications SET challenge_expires_at=now()-interval '1 second' WHERE user_id='{id}'");
        Assert.Equal((HttpStatusCode.Unauthorized, "FIRST_LOGIN_CHALLENGE_INVALID"),
            await PostJson(client, "/api/auth/first-login/verify", new { challengeToken = token, code = lastCode }) is var expired ? (expired.Status, expired.Body.GetProperty("code").GetString()) : default);
        var again = await ChallengeAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/auth/first-login/verify", new { challengeToken = again, code = lastCode })).Status);
    }

    [PostgresFact]
    public async Task A_mistyped_expert_email_is_fixed_by_the_admin_and_cannot_be_taken_over_by_email()
    {
        var other = await CreateExpertAsync();
        var (id, typo) = await CreateExpertAsync();
        await ChallengeAsync(typo, password);
        Assert.Equal(typo, (await SentCodeAsync())!.Email);
        // Whoever holds the mistyped mailbox cannot reset the password: no reset link exists for an unverified account.
        using (var scope = factory!.Services.CreateScope())
            Assert.Null(await scope.ServiceProvider.GetRequiredService<ILocalPasswordReset>().CreateLinkAsync(typo, default));

        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var account = await client.GetAsync($"/api/accounts/{id}");
        var etag = account.Headers.ETag!.Tag;
        var fixedEmail = $"fixed-{Guid.NewGuid():N}@example.test";
        Assert.Equal((HttpStatusCode)428, (await client.PatchAsJsonAsync($"/api/accounts/{id}/email", new { email = fixedEmail })).StatusCode);
        HttpRequestMessage Patch(string tag, string value)
        {
            var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/accounts/{id}/email") { Content = JsonContent.Create(new { email = value }) };
            request.Headers.TryAddWithoutValidation("If-Match", tag);
            return request;
        }
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(Patch("\"999\"", fixedEmail))).StatusCode);
        var taken = await client.SendAsync(Patch(etag, other.Email));
        Assert.Equal((HttpStatusCode.Conflict, "EMAIL_EXISTS"), (taken.StatusCode, (await taken.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()));
        var fixedResponse = await client.SendAsync(Patch(etag, fixedEmail));
        Assert.Equal(HttpStatusCode.OK, fixedResponse.StatusCode);
        Assert.NotEqual(etag, fixedResponse.Headers.ETag!.Tag);
        Assert.Equal(fixedEmail, (await fixedResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("email").GetString());
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM first_login_verifications WHERE user_id='{id}'"));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{id}' AND action='Update'"));
        client.DefaultRequestHeaders.Authorization = null;

        // The old address no longer signs in; the new one receives the code and finishes the first sign-in.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostJson(client, "/api/auth/login", new { email = typo, password })).Status);
        var token = await ChallengeAsync(fixedEmail, password);
        var job = await SentCodeAsync();
        Assert.Equal(fixedEmail, job!.Email);
        Assert.Equal(HttpStatusCode.OK, (await PostJson(client, "/api/auth/first-login/verify", new { challengeToken = token, code = job.Code })).Status);

        // A verified email is never rewritten: by the API (409) or directly (trigger).
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var current = (await client.GetAsync($"/api/accounts/{id}")).Headers.ETag!.Tag;
        var verified = await client.SendAsync(Patch(current, $"late-{Guid.NewGuid():N}@example.test"));
        Assert.Equal((HttpStatusCode.Conflict, "EMAIL_ALREADY_VERIFIED"), (verified.StatusCode, (await verified.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()));
        // The API role cannot either, even with UPDATE(email): the trigger refuses it (the owner and superusers are not limited).
        var apiRole = $"fire3d_api_probe_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE ROLE {apiRole} NOLOGIN IN ROLE fire3d_api");
        try
        {
            var refused = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                ExecuteAsync($"SET ROLE {apiRole}; UPDATE users SET email='direct-{Guid.NewGuid():N}@example.test' WHERE id='{id}'"));
            Assert.Equal("23514", refused.SqlState);
        }
        finally { await ExecuteAsync($"DROP ROLE {apiRole}"); }
    }

    [PostgresFact]
    public async Task Google_of_the_account_email_finishes_the_first_sign_in_and_other_roles_are_unchanged()
    {
        var (id, email) = await CreateExpertAsync();
        var google = new FixedGoogle(new()
        {
            ["same"] = new($"google-{id:N}", email.ToUpperInvariant()),
            ["other"] = new("google-someone", "someone@example.test")
        });
        using var host = factory!.WithWebHostBuilder(web => web.ConfigureTestServices(services =>
        {
            services.RemoveAll<IIdentityProvider>();
            services.AddSingleton<IIdentityProvider>(google);
        }));
        using var http = host.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var token = await ChallengeAsync(email, password, http);
        // Google alone never starts the flow: the challenge comes from the password.
        Assert.Equal("FIRST_LOGIN_CHALLENGE_INVALID", (await PostJson(http, "/api/auth/first-login/google", new { challengeToken = "not-a-challenge", idToken = "same" })).Body.GetProperty("code").GetString());
        var mismatch = await PostJson(http, "/api/auth/first-login/google", new { challengeToken = token, idToken = "other" });
        Assert.Equal((HttpStatusCode.Forbidden, "GOOGLE_EMAIL_MISMATCH"), (mismatch.Status, mismatch.Body.GetProperty("code").GetString()));
        var signedIn = await PostJson(http, "/api/auth/first-login/google", new { challengeToken = token, idToken = "same" });
        Assert.Equal(HttpStatusCode.OK, signedIn.Status);
        Assert.Equal(("ChangePassword", true), (signedIn.Body.GetProperty("user").GetProperty("firstLoginStep").GetString(), signedIn.Body.GetProperty("user").GetProperty("googleLinked").GetBoolean()));
        Assert.Equal($"google-{id:N}", (string?)await ScalarAsync($"SELECT firebase_uid FROM users WHERE id='{id}'"));
        Assert.NotEqual(DBNull.Value, await ScalarAsync($"SELECT email_verified_at FROM users WHERE id='{id}'"));

        // Only experts are affected: a Trainee created by PlatformAdmin signs in directly.
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var trainee = $"trainee-{Guid.NewGuid():N}@example.test";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/accounts", new { email = trainee, password, role = "Trainee", username = $"t{Guid.NewGuid():N}"[..20] })).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        var direct = await PostJson(client, "/api/auth/login", new { email = trainee, password });
        Assert.Equal(HttpStatusCode.OK, direct.Status);
        Assert.Equal(JsonValueKind.Null, direct.Body.GetProperty("user").GetProperty("firstLoginStep").ValueKind);
    }
}
