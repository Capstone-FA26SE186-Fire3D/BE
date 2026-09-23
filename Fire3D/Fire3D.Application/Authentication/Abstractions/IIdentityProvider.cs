using System.Threading;
using System.Threading.Tasks;

namespace Fire3D.Application.Authentication.Abstractions;

public record IdentityUser(string Uid, string Email, string FullName);
public record VerifiedIdentity(string Uid, string Email);

public sealed class IdentityProviderException(string code, string message, int status) : Exception(message)
{
    public AuthError Error { get; } = new(code, message, status);
    public static IdentityProviderException InvalidToken() => new("INVALID_FIREBASE_TOKEN", "Invalid Google ID token.", 401);
    public static IdentityProviderException Unavailable() => new("IDENTITY_UNAVAILABLE", "Identity service is temporarily unavailable.", 503);
}

public interface IIdentityProvider
{
    Task<IdentityUser> CreateEmailPasswordUserAsync(
        string email,
        string password,
        string fullName,
        CancellationToken ct);

    Task<VerifiedIdentity> SignInWithPasswordAsync(
        string email,
        string password,
        CancellationToken ct);

    Task<VerifiedIdentity> VerifyGoogleTokenAsync(
        string idToken,
        CancellationToken ct);

    Task DeleteUserAsync(
        string uid,
        CancellationToken ct);
}
