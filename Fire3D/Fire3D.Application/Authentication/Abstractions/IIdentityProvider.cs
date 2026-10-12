using System.Threading;
using System.Threading.Tasks;

namespace Fire3D.Application.Authentication.Abstractions;

public record IdentityUser(string Uid, string Email, string FullName);
/// <param name="AuthTime">When the user last signed in with Google (Firebase auth_time), if the token states it.</param>
public record VerifiedIdentity(string Uid, string Email, string? DisplayName = null, DateTimeOffset? AuthTime = null);

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
