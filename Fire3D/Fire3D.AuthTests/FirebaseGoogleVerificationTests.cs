using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Infrastructure.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using System.Reflection;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class FirebaseGoogleVerificationTests
{
    private sealed class Verifier(Func<bool, CancellationToken, Task<VerifiedFirebaseClaims>> verify) : IFirebaseGoogleTokenVerifier
    {
        public Task<VerifiedFirebaseClaims> VerifyAsync(string token, bool checkRevoked, CancellationToken ct)
            => verify(checkRevoked, ct);
    }

    private static Dictionary<string, object> Claims(string provider = "google.com", object? verified = null)
        => new() { ["email"] = " User@Example.Test ", ["email_verified"] = verified ?? true,
            ["firebase"] = new Dictionary<string, object> { ["sign_in_provider"] = provider } };

    private static FirebaseIdentityProvider Provider(IFirebaseGoogleTokenVerifier verifier, TimeProvider? clock = null)
        => new(new HttpClient(), Options.Create(new AuthEmailOptions()), NullLogger<FirebaseIdentityProvider>.Instance,
            verifier, clock ?? TimeProvider.System);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verified_google_claims_require_revocation_check_and_normalize_email(bool jsonClaims)
    {
        var claims = Claims();
        if (jsonClaims) claims["firebase"] = JObject.Parse("{\"sign_in_provider\":\"google.com\"}");
        var identity = await Provider(new Verifier((checkRevoked, _) =>
        {
            Assert.True(checkRevoked);
            return Task.FromResult(new VerifiedFirebaseClaims("google-uid", claims));
        })).VerifyGoogleTokenAsync("id-token", default);
        Assert.Equal("google-uid", identity.Uid);
        Assert.Equal("user@example.test", identity.Email);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("anonymous")]
    [InlineData("facebook.com")]
    [InlineData("")]
    public async Task Other_sign_in_providers_are_invalid(string provider)
        => await AssertInvalid(Claims(provider));

    [Theory]
    [InlineData(false)]
    [InlineData("true")]
    public async Task Email_must_be_verified_boolean(object verified)
        => await AssertInvalid(Claims(verified: verified));

    [Theory]
    [InlineData("email")]
    [InlineData("email_verified")]
    [InlineData("firebase")]
    public async Task Missing_required_claim_is_invalid(string claim)
    {
        var claims = Claims(); claims.Remove(claim);
        await AssertInvalid(claims);
    }

    private static async Task AssertInvalid(Dictionary<string, object> claims)
    {
        var error = await Assert.ThrowsAsync<GoogleIdentityException>(() =>
            Provider(new Verifier((_, _) => Task.FromResult(new VerifiedFirebaseClaims("uid", claims))))
                .VerifyGoogleTokenAsync("id-token", default));
        Assert.Equal(GoogleIdentityFailure.InvalidToken, error.Failure);
    }

    [Fact]
    public async Task Deadline_is_15_seconds_even_when_sdk_does_not_cooperate_with_cancellation()
    {
        var clock = new ManualDeadlineClock();
        var unresolved = new TaskCompletionSource<VerifiedFirebaseClaims>(TaskCreationOptions.RunContinuationsAsynchronously);
        var verification = Provider(new Verifier((_, _) => unresolved.Task), clock).VerifyGoogleTokenAsync("token", default);
        Assert.Equal(TimeSpan.FromSeconds(15), clock.DueTime);
        clock.Expire();
        var error = await Assert.ThrowsAsync<GoogleIdentityException>(() => verification.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(GoogleIdentityFailure.ProviderUnavailable, error.Failure);
        unresolved.SetResult(new("uid", Claims()));
    }

    [Fact]
    public async Task Request_cancellation_propagates_instead_of_becoming_503()
    {
        using var cancellation = new CancellationTokenSource();
        var verification = Provider(new Verifier((_, ct) =>
            Task.Delay(Timeout.Infinite, ct).ContinueWith<VerifiedFirebaseClaims>(_ => throw new OperationCanceledException(ct), ct)))
            .VerifyGoogleTokenAsync("token", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verification);
    }

    [Fact]
    public async Task Network_failure_is_unavailable_without_exposing_provider_message()
    {
        var error = await Assert.ThrowsAsync<GoogleIdentityException>(() =>
            Provider(new Verifier((_, _) => throw new HttpRequestException("secret-token-and-email")))
                .VerifyGoogleTokenAsync("token", default));
        Assert.Equal(GoogleIdentityFailure.ProviderUnavailable, error.Failure);
        Assert.DoesNotContain("secret-token", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(AuthErrorCode.RevokedIdToken, GoogleIdentityFailure.InvalidToken)]
    [InlineData(AuthErrorCode.ExpiredIdToken, GoogleIdentityFailure.InvalidToken)]
    [InlineData(AuthErrorCode.InvalidIdToken, GoogleIdentityFailure.InvalidToken)]
    [InlineData(AuthErrorCode.UserNotFound, GoogleIdentityFailure.InvalidToken)]
    [InlineData(AuthErrorCode.CertificateFetchFailed, GoogleIdentityFailure.ProviderUnavailable)]
    [InlineData(AuthErrorCode.UnexpectedResponse, GoogleIdentityFailure.ProviderUnavailable)]
    public async Task Sdk_errors_distinguish_invalid_identity_from_provider_outage(AuthErrorCode code, GoogleIdentityFailure expected)
    {
        // FirebaseAuthException has an internal constructor; construct the actual SDK exception without network calls.
        var sdkError = (FirebaseAuthException)Activator.CreateInstance(typeof(FirebaseAuthException),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object?[] { ErrorCode.InvalidArgument, "sensitive-provider-message", code, null, null }, null)!;
        var error = await Assert.ThrowsAsync<GoogleIdentityException>(() =>
            Provider(new Verifier((_, _) => throw sdkError)).VerifyGoogleTokenAsync("token", default));
        Assert.Equal(expected, error.Failure);
        Assert.DoesNotContain("sensitive", error.Message);
    }

    private sealed class ManualDeadlineClock : TimeProvider
    {
        private TimerCallback? callback;
        private object? state;
        public TimeSpan DueTime { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            this.callback = callback; this.state = state; DueTime = dueTime;
            return new ManualTimer();
        }
        public void Expire() => callback!(state);
        private sealed class ManualTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
