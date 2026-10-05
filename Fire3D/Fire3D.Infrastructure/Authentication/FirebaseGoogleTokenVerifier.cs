using FirebaseAdmin.Auth;

namespace Fire3D.Infrastructure.Authentication;

public sealed record VerifiedFirebaseClaims(string Uid, IReadOnlyDictionary<string, object> Claims);

// Only the SDK implementation may produce trusted claims in runtime DI.
public interface IFirebaseGoogleTokenVerifier
{
    Task<VerifiedFirebaseClaims> VerifyAsync(string idToken, bool checkRevoked, CancellationToken ct);
}

public sealed class FirebaseGoogleTokenVerifier : IFirebaseGoogleTokenVerifier
{
    public async Task<VerifiedFirebaseClaims> VerifyAsync(string idToken, bool checkRevoked, CancellationToken ct)
    {
        var token = await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(idToken, checkRevoked, ct);
        return new(token.Uid, token.Claims);
    }
}
