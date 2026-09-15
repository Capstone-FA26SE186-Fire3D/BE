using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.Login;

internal sealed class LoginCommandHandler(IAuthStore store, IPasswordService passwords, ITokenService tokens, TimeProvider clock)
    : IRequestHandler<LoginCommand, AuthResult<TokenResponse>>
{
    public async Task<AuthResult<TokenResponse>> Handle(LoginCommand command, CancellationToken ct)
    {
        var request = command.Request;
        var email = AuthSupport.NormalizeEmail(request.Email);
        if (email is null || request.Password is null || request.Password.Length is < 1 or > 128)
            return AuthSupport.InvalidCredentials();
        var user = await store.FindUserByEmailAsync(email, ct);
        if (user is null)
        {
            passwords.VerifyDummy(request.Password);
            return AuthSupport.InvalidCredentials();
        }
        if (!passwords.Verify(user, request.Password, out var rehash)) return AuthSupport.InvalidCredentials();
        var verifiedHash = user.PasswordHash;
        await using var transaction = await store.BeginUserTransactionAsync(user.Id, ct);
        user = await store.FindUserAsync(user.Id, ct);
        if (user is null || user.PasswordHash != verifiedHash || !await AuthSupport.IsActiveAsync(store, user, ct))
            return AuthSupport.InvalidCredentials();
        var now = AuthSupport.UtcNow(clock);
        await store.UpdateLoginAsync(user.Id, now, rehash ? passwords.Hash(user, request.Password) : verifiedHash, ct);
        var response = await AuthSupport.IssueAsync(store, tokens, user, Guid.NewGuid(), now, now.Add(tokens.RefreshTokenLifetime), ct);
        await store.WriteAuditAsync(user, "Login", user.Id, now, ct);
        await transaction.CommitAsync(ct);
        return AuthResult<TokenResponse>.Ok(response);
    }
}
