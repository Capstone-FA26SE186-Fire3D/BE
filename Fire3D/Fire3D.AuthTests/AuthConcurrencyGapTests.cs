using System.Net;
using System.Net.Http.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.ChangePassword;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // AUTH-01: two verified emails race for one username across both registration routes.
    [PostgresFact]
    public async Task Concurrent_registrations_for_one_username_have_one_winner_and_the_loser_keeps_its_proof()
    {
        for (var round = 0; round < 3; round++)
        {
            var username = $"race.user{round}";
            var traineeEmail = $"race-trainee{round}@example.test"; var ownerEmail = $"race-owner{round}@example.test";
            var traineeProof = await VerifyRegistrationEmailAsync(traineeEmail);
            var ownerProof = await VerifyRegistrationEmailAsync(ownerEmail);
            var trainee = new
            {
                email = traineeEmail, username, password = "StrongPassword12!", confirmPassword = "StrongPassword12!",
                fullName = "Race trainee", registrationToken = traineeProof
            };
            var owner = new
            {
                email = ownerEmail, username = username.ToUpperInvariant(), password = "StrongPassword12!", confirmPassword = "StrongPassword12!",
                fullName = "Race owner", organizationName = $"Race Co {round}", organizationAddress = "1 Race Street",
                organizationPhoneNumber = $"+8412345{round:D4}", registrationToken = ownerProof
            };
            var responses = await Task.WhenAll(
                client.PostAsJsonAsync("/api/auth/register/trainee", trainee),
                client.PostAsJsonAsync("/api/auth/register/organization", owner));
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
            var loser = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
            Assert.Contains("USERNAME_EXISTS", await loser.Content.ReadAsStringAsync());
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM users WHERE lower(username)='{username}'"));

            // The losing form left no partial account or organization, and its proof still completes registration.
            var traineeLost = responses[0].StatusCode == HttpStatusCode.Conflict;
            var retry = traineeLost
                ? await client.PostAsJsonAsync("/api/auth/register/trainee", trainee with { username = username + ".b" })
                : await client.PostAsJsonAsync("/api/auth/register/organization", owner with { username = username + ".b" });
            Assert.True(retry.StatusCode == HttpStatusCode.Created, await retry.Content.ReadAsStringAsync());
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM organizations WHERE name='Race Co {round}'"));
        }
    }

    // AUTH-04: change password commits hash, token invalidation, session revocation and audit together.
    [PostgresFact]
    public async Task Change_password_rolls_back_on_audit_failure_and_concurrent_changes_have_one_winner()
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var account = await CreateAccountAsync("changer@example.test", UserRole.Trainee);
        client.DefaultRequestHeaders.Authorization = null;
        var originalHash = await ScalarAsync($"SELECT password_hash FROM users WHERE id='{account.Id}'");
        var session = await LoginAsync("changer@example.test");

        await ExecuteAsync($$"""
            CREATE FUNCTION test_fail_change_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'audit unavailable'; END $$;
            CREATE TRIGGER test_fail_change_audit BEFORE INSERT ON audit_logs FOR EACH ROW
             WHEN (NEW.target_id='{{account.Id}}' AND NEW.action='Update') EXECUTE FUNCTION test_fail_change_audit();
            """);
        try
        {
            using var failed = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password")
            { Content = JsonContent.Create(new ChangePasswordRequest(password, "never-saved-1")) };
            failed.Headers.Authorization = new("Bearer", session.AccessToken);
            Assert.Equal(HttpStatusCode.InternalServerError, (await client.SendAsync(failed)).StatusCode);
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER test_fail_change_audit ON audit_logs; DROP FUNCTION test_fail_change_audit();");
        }
        Assert.Equal(originalHash, await ScalarAsync($"SELECT password_hash FROM users WHERE id='{account.Id}'"));
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM auth_refresh_tokens WHERE user_id='{account.Id}' AND revoked_at IS NOT NULL"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(session.RefreshToken))).StatusCode);

        var first = await LoginAsync("changer@example.test");
        var second = await LoginAsync("changer@example.test");
        async Task<HttpResponseMessage> Change(string accessToken, string next)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password")
            { Content = JsonContent.Create(new ChangePasswordRequest(password, next)) };
            request.Headers.Authorization = new("Bearer", accessToken);
            return await client.SendAsync(request);
        }
        var results = await Task.WhenAll(Change(first.AccessToken, "winner-pass-1"), Change(second.AccessToken, "winner-pass-2"));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.NoContent);
        // The loser either lost its session to the winner's revocation (401) or no longer knows the current password (400).
        Assert.Single(results, x => x.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest);
        var winner = results[0].StatusCode == HttpStatusCode.NoContent ? "winner-pass-1" : "winner-pass-2";
        var loser = winner == "winner-pass-1" ? "winner-pass-2" : "winner-pass-1";
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("changer@example.test", winner))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("changer@example.test", loser))).StatusCode);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{account.Id}' AND action='Update'"));
    }
}
