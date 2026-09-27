using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using MediatR;
namespace Fire3D.Application.Authentication.Commands.FirebaseLogin;

public record ExchangeFirebaseTokenCommand(string IdToken) : IRequest<AuthResult<GoogleExchangeResponse>>;
public sealed class ExchangeFirebaseTokenCommandHandler(IAuthStore store, ITokenService tokens,
    IIdentityProvider identityProvider, TimeProvider clock) : IRequestHandler<ExchangeFirebaseTokenCommand,AuthResult<GoogleExchangeResponse>>
{
    public async Task<AuthResult<GoogleExchangeResponse>> Handle(ExchangeFirebaseTokenCommand request,CancellationToken ct)
    {
        var authenticatedAt = AuthSupport.UtcNow(clock);
        if (string.IsNullOrWhiteSpace(request.IdToken) || request.IdToken.Length>16384)
            return AuthResult<GoogleExchangeResponse>.Fail("INVALID_FIREBASE_TOKEN","Google ID token is required.",400);
        VerifiedIdentity identity;
        try { identity = await identityProvider.VerifyGoogleTokenAsync(request.IdToken,ct); }
        catch (OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
        catch (Exception) { return AuthResult<GoogleExchangeResponse>.Fail("INVALID_FIREBASE_TOKEN","Invalid Firebase ID token.",401); }
        var email = PasswordResetValidation.NormalizeEmail(identity.Email);
        if (email is null) return AuthResult<GoogleExchangeResponse>.Fail("INVALID_FIREBASE_TOKEN","Verified email is required.",401);
        var existing = await store.FindUserByFirebaseUidAsync(identity.Uid,ct);
        if (existing is null)
        {
            // A verified Google identity is not enough to choose a role or tenant. Onboarding
            // must collect the account type and any required Trainee/organization details.
            if (await store.FindUserByEmailAsync(email,ct) is not null)
                return AuthResult<GoogleExchangeResponse>.Fail("ACCOUNT_LINK_REQUIRED","Sign in with your existing account. Explicit Google linking is required.",409);
            return AuthResult<GoogleExchangeResponse>.Ok(new("OnboardingRequired"));
        }
        await using var tx = await store.BeginUserTransactionAsync(existing.Id,ct);
        var user = await store.FindUserByFirebaseUidAsync(identity.Uid,ct);
        if (user is null)
            return AuthResult<GoogleExchangeResponse>.Fail("ACCOUNT_CHANGED","Account changed concurrently. Retry sign-in.",409);
        if (!await AuthSupport.IsActiveAsync(store,user,ct))
            return AuthResult<GoogleExchangeResponse>.Fail("ACCOUNT_DISABLED","Account or organization is unavailable.",403);
        await store.UpdateLoginAsync(user.Id,authenticatedAt,ct);
        var response = await AuthSupport.IssueAsync(store,tokens,user,Guid.NewGuid(),authenticatedAt,
            authenticatedAt.Add(tokens.RefreshTokenLifetime),ct);
        await store.WriteAuditAsync(user,"Login",user.Id,authenticatedAt,ct);
        await tx.CommitAsync(ct);
        return AuthResult<GoogleExchangeResponse>.Ok(new("Authenticated", response));
    }
}
