using System.Net.Mail;
namespace Fire3D.Application.Authentication;

public static class PasswordResetValidation
{
    public static string? NormalizeEmail(string? email)
    {
        var value = email?.Trim().ToLowerInvariant();
        return value is not null && value.Length <= 254 && MailAddress.TryCreate(value, out var parsed)
            && parsed.Address == value && value.Contains('@') && !value.Any(char.IsWhiteSpace) ? value : null;
    }
    public static bool ValidPassword(string? password) => !string.IsNullOrWhiteSpace(password) && password.Length is >= 12 and <= 128;
}

public sealed class PasswordResetException(string code, string message, int status, bool permanent = false) : Exception(message)
{
    public AuthError Error { get; } = new(code, message, status);
    public bool Permanent { get; } = permanent;
    public static PasswordResetException Unavailable() => new("IDENTITY_UNAVAILABLE", "Identity service is temporarily unavailable.", 503);
    public static PasswordResetException InvalidCode() => new("INVALID_RESET_CODE", "The reset code is invalid, expired or already used.", 400, true);
}

public interface IPasswordResetStore
{
    // Commits revocation and a durable session fence before external password mutation.
    Task<Guid?> BeginAsync(Guid userId, string firebaseUid, string codeHash, CancellationToken ct);
    Task FinishAsync(Guid operationId, Guid userId, string outcome, CancellationToken ct);
}

public sealed record ResetEmailJob(Guid Id, string Email, Guid LeaseToken, int Attempt);
public interface IPasswordResetQueue
{
    Task<ResetEmailJob?> ClaimAsync(CancellationToken ct);
    Task CompleteAsync(ResetEmailJob job, CancellationToken ct);
    Task FailAsync(ResetEmailJob job, bool permanent, CancellationToken ct);
}
