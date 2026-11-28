using Fire3D.Domain.Entities;

namespace Fire3D.Application.Authentication;

/// <summary>
/// BE#235: a FireSafetyExpert account created by PlatformAdmin starts in VerifyEmail. After the correct password, the login answers
/// 403 FIRST_LOGIN_VERIFICATION_REQUIRED with a challenge token and a 6-digit code is emailed; the code (or a Google identity of the same
/// email) opens a session in ChangePassword, which may only change the password, read /api/auth/me and log out. Changing the password
/// clears the step and revokes every session, so the next sign-in is a normal one.
/// </summary>
public static class FirstLoginSteps
{
    public const string VerifyEmail = "VerifyEmail";
    public const string ChangePassword = "ChangePassword";

    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);
    public const int SendsPerHour = 5;
    public const int MaxAttempts = 5;

    /// <summary>The login refusal that carries the challenge (problem extensions challengeToken, codeExpiresAt, ...).</summary>
    public static AuthResult<T> Required<T>(FirstLoginChallenge challenge) => new(default, new AuthError("FIRST_LOGIN_VERIFICATION_REQUIRED",
        "Enter the code sent to the account email (or sign in with Google using that email) to finish the first sign-in.", 403,
        Extensions: Extensions(challenge)));

    public static IReadOnlyDictionary<string, object?> Extensions(FirstLoginChallenge challenge) => new Dictionary<string, object?>
    {
        ["challengeToken"] = challenge.ChallengeToken, ["challengeExpiresAt"] = challenge.ChallengeExpiresAt,
        ["codeExpiresAt"] = challenge.CodeExpiresAt, ["emailHint"] = challenge.EmailHint, ["resendAfterSeconds"] = challenge.ResendAfterSeconds
    };

    /// <summary>a***@example.com: enough for the user to recognise the address, not enough to learn it.</summary>
    public static string EmailHint(string email)
    {
        var at = email.IndexOf('@');
        return at <= 0 ? "***" : email[0] + "***" + email[at..];
    }
}

/// <summary>What the client needs to finish the first sign-in. ResendAfterSeconds is 0 when a new code may be requested now.</summary>
public sealed record FirstLoginChallenge(string ChallengeToken, DateTime ChallengeExpiresAt, DateTime CodeExpiresAt, string EmailHint, int ResendAfterSeconds);
public sealed record FirstLoginVerifyRequest(string ChallengeToken, string Code);
public sealed record FirstLoginGoogleRequest(string ChallengeToken, string IdToken);
public sealed record FirstLoginResendRequest(string ChallengeToken);
public sealed record FirstLoginEmailJob(Guid Id, string Email, string Code, Guid LeaseToken, int Attempt);

public interface IFirstLoginVerification
{
    /// <summary>Called by the password login inside its user transaction, after the password and lifecycle checks.</summary>
    Task<AuthResult<FirstLoginChallenge>> StartAsync(User user, DateTime now, CancellationToken ct);
    Task<AuthResult<LoginResponse>> VerifyCodeAsync(FirstLoginVerifyRequest request, CancellationToken ct);
    Task<AuthResult<LoginResponse>> VerifyGoogleAsync(FirstLoginGoogleRequest request, CancellationToken ct);
    Task<AuthResult<FirstLoginChallenge>> ResendAsync(FirstLoginResendRequest request, CancellationToken ct);
}

/// <summary>BE#245: the job's code cannot be decrypted (the key derived from Jwt:SigningKey changed); the job is already Dead.</summary>
public sealed class FirstLoginCodeUnreadableException(Guid jobId) : Exception("A first sign-in code email job cannot be decrypted and was marked Dead.")
{
    public Guid JobId { get; } = jobId;
}

public interface IFirstLoginDeliveryQueue
{
    Task<FirstLoginEmailJob?> ClaimAsync(CancellationToken ct);
    Task CompleteAsync(FirstLoginEmailJob job, CancellationToken ct);
    Task FailAsync(FirstLoginEmailJob job, bool permanent, CancellationToken ct);
}
