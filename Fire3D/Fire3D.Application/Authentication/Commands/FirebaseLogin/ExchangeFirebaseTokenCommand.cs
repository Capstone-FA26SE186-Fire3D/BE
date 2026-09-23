using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using MediatR;
namespace Fire3D.Application.Authentication.Commands.FirebaseLogin;

public record ExchangeFirebaseTokenCommand(string IdToken) : IRequest<AuthResult<TokenResponse>>;
public sealed class ExchangeFirebaseTokenCommandHandler(IAuthStore store, ITokenService tokens,
    IIdentityProvider identityProvider, TimeProvider clock) : IRequestHandler<ExchangeFirebaseTokenCommand,AuthResult<TokenResponse>>
{
    public async Task<AuthResult<TokenResponse>> Handle(ExchangeFirebaseTokenCommand request,CancellationToken ct)
    {
        var authenticatedAt = AuthSupport.UtcNow(clock);
        if (string.IsNullOrWhiteSpace(request.IdToken) || request.IdToken.Length>16384)
            return AuthResult<TokenResponse>.Fail("INVALID_FIREBASE_TOKEN","Google ID token is required.",400);
        VerifiedIdentity identity;
        try { identity = await identityProvider.VerifyGoogleTokenAsync(request.IdToken,ct); }
        catch (OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
        catch (IdentityProviderException ex) { return AuthResult<TokenResponse>.Fail(ex.Error.Code, ex.Error.Message, ex.Error.Status); }
        var email = PasswordResetValidation.NormalizeEmail(identity.Email);
        if (email is null) return AuthResult<TokenResponse>.Fail("INVALID_FIREBASE_TOKEN","Verified email is required.",401);
        var existing = await store.FindUserByFirebaseUidAsync(identity.Uid,ct);
        var id = existing?.Id ?? Guid.NewGuid();
        await using var tx = await store.BeginUserTransactionAsync(id,ct);
        var user = await store.FindUserByFirebaseUidAsync(identity.Uid,ct);
        if (user is null)
        {
            // Email alone does not authorize linking an existing local account.
            if (await store.FindUserByEmailAsync(email,ct) is not null)
                return AuthResult<TokenResponse>.Fail("ACCOUNT_LINK_REQUIRED","Sign in with your existing account. Explicit Google linking is required.",409);
            user = new User { Id=id,Email=email,FirebaseUid=identity.Uid,Role=UserRole.Trainee,
                IsActive=true,CreatedAt=authenticatedAt,UpdatedAt=authenticatedAt };
            if (!await store.TryCreateUserAsync(user,ct))
                return AuthResult<TokenResponse>.Fail("ACCOUNT_EXISTS","Account changed concurrently. Retry sign-in.",409);
            await store.WriteAuditAsync(user,"Create",user.Id,authenticatedAt,ct);
        }
        if (!await AuthSupport.IsActiveAsync(store,user,ct))
            return AuthResult<TokenResponse>.Fail("ACCOUNT_DISABLED","Account or organization is unavailable.",403);
        await store.UpdateLoginAsync(user.Id,authenticatedAt,ct);
        var response = await AuthSupport.IssueAsync(store,tokens,user,Guid.NewGuid(),authenticatedAt,
            authenticatedAt.Add(tokens.RefreshTokenLifetime),ct);
        await store.WriteAuditAsync(user,"Login",user.Id,authenticatedAt,ct);
        await tx.CommitAsync(ct);
        return AuthResult<TokenResponse>.Ok(response);
    }
}
