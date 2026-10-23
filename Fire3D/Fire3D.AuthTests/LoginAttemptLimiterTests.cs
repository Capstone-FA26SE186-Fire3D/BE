using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Authentication;
using Xunit;

namespace Fire3D.AuthTests;

public class LoginAttemptLimiterTests
{
    private static readonly DateTime Start = new(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Failures_lock_one_email_until_the_window_ends_and_success_resets()
    {
        var limiter = new MemoryLoginAttemptLimiter(new LoginAttemptOptions { MaxFailures = 3, WindowMinutes = 15 });
        for (var i = 0; i < 2; i++) limiter.RecordFailure("a@example.test", Start.AddMinutes(i));
        Assert.Null(limiter.RetryAfterSeconds("a@example.test", Start.AddMinutes(2)));
        limiter.RecordFailure("a@example.test", Start.AddMinutes(2));
        // Locked until 15 minutes after the first failure of the window; another email is unaffected.
        Assert.Equal(600, limiter.RetryAfterSeconds("a@example.test", Start.AddMinutes(5)));
        Assert.Null(limiter.RetryAfterSeconds("b@example.test", Start.AddMinutes(5)));
        Assert.Null(limiter.RetryAfterSeconds("a@example.test", Start.AddMinutes(15)));
        // A new window starts after expiry, and a successful sign-in clears the count.
        limiter.RecordFailure("a@example.test", Start.AddMinutes(16));
        limiter.RecordFailure("a@example.test", Start.AddMinutes(16));
        limiter.Reset("a@example.test");
        limiter.RecordFailure("a@example.test", Start.AddMinutes(17));
        Assert.Null(limiter.RetryAfterSeconds("a@example.test", Start.AddMinutes(17)));
    }

    [Fact]
    public void Retry_after_is_at_least_one_second_and_invalid_options_fall_back_to_one()
    {
        var limiter = new MemoryLoginAttemptLimiter(new LoginAttemptOptions { MaxFailures = 0, WindowMinutes = 0 });
        limiter.RecordFailure("a@example.test", Start);
        Assert.Equal(1, limiter.RetryAfterSeconds("a@example.test", Start.AddSeconds(59.9)));
        Assert.Null(limiter.RetryAfterSeconds("a@example.test", Start.AddMinutes(1)));
    }
}

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Login_locks_an_email_after_ten_failures_without_revealing_or_blocking_other_accounts()
    {
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.test", "wrong-" + i))).StatusCode);
        var locked = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.test", password));
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("AUTH_LOGIN_RATE_LIMITED", (await locked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.InRange(int.Parse(locked.Headers.GetValues("Retry-After").Single()), 1, 900);
        // Unknown emails are counted per email too and other accounts are not affected.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody@example.test", "wrong"))).StatusCode);
    }

    [PostgresFact]
    public async Task Responses_carry_security_headers_and_no_server_banner()
    {
        var response = await client.GetAsync("/health/live");
        Assert.Equal("max-age=31536000; includeSubDomains", response.Headers.GetValues("Strict-Transport-Security").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(response.Headers.Contains("Server"));
    }
}
