using System.Threading;
using System.Threading.Tasks;

namespace Fire3D.Application.Authentication.Abstractions;

public record IdentityUser(string Uid, string Email, string FullName);
public record VerifiedIdentity(string Uid, string Email);

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
