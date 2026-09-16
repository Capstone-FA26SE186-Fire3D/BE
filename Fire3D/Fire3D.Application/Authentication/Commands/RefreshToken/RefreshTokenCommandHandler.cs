using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.RefreshToken;

internal sealed class RefreshTokenCommandHandler(IAuthStore store, ITokenService tokens, TimeProvider clock)
    : IRequestHandler<RefreshTokenCommand, AuthResult<TokenResponse>>
{
    public async Task<AuthResult<TokenResponse>> Handle(RefreshTokenCommand command, CancellationToken ct)
    {
        var rawToken = command.RefreshToken;
        if (string.IsNullOrWhiteSpace(rawToken) || rawToken.Length > 256) return AuthSupport.InvalidRefresh();
        var hash = tokens.HashRefreshToken(rawToken);
        var token = await store.FindRefreshTokenAsync(hash, ct);
        if (token is null) return AuthSupport.InvalidRefresh();
        await using var transaction = await store.BeginUserTransactionAsync(token.UserId, ct);
        token = await store.FindRefreshTokenAsync(hash, ct);
        if (token is null) return AuthSupport.InvalidRefresh();
        var now = AuthSupport.UtcNow(clock);
        // Commit revocation even when returning 401: rolling it back would enable replay.
        if (token.ConsumedAt.HasValue || token.RevokedAt.HasValue)
        {
            await store.RevokeFamilyAsync(token.UserId, token.FamilyId, now, ct);
            await transaction.CommitAsync(ct);
            return AuthSupport.InvalidRefresh();
        }
        if (token.ExpiresAt <= now) return AuthSupport.InvalidRefresh();
        var user = await store.FindUserAsync(token.UserId, ct);
        if (user is null || !await AuthSupport.IsActiveAsync(store, user, ct))
        {
            await store.RevokeFamilyAsync(token.UserId, token.FamilyId, now, ct);
            await transaction.CommitAsync(ct);
            return AuthSupport.InvalidRefresh();
        }
        await store.ConsumeRefreshTokenAsync(token.Id, now, ct);
        // Rotation preserves the original absolute session expiry.
        var response = await AuthSupport.IssueAsync(store, tokens, user, token.FamilyId, now, token.ExpiresAt, ct);
        await transaction.CommitAsync(ct);
        return AuthResult<TokenResponse>.Ok(response);
    }
}
