using System;
using System.Threading;
using System.Threading.Tasks;

using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;

namespace Fire3D.Application.Authentication.Services;

public class Fire3DSessionIssuer(IAuthStore authStore, ITokenService tokens, TimeProvider clock)
{
    public async Task<LoginResponse> IssueSessionAsync(User user, CancellationToken ct)
    {
        if (user.IsActive == false)
            throw new Exception("AccountDisabled");

        var now = clock.GetUtcNow().UtcDateTime;

        await using var transaction = await authStore.BeginUserTransactionAsync(user.Id, ct);

        // Update last login
        await authStore.UpdateLoginAsync(user.Id, now, ct);

        // Issue tokens
        var familyId = Guid.NewGuid();
        var accessToken = tokens.CreateAccessToken(user, familyId, now);
        var refreshTokenStr = tokens.CreateRefreshToken();
        var refreshTokenHash = tokens.HashRefreshToken(refreshTokenStr);

        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = refreshTokenHash,
            FamilyId = familyId,
            CreatedAt = now,
            ExpiresAt = now.Add(tokens.RefreshTokenLifetime),
            
        };

        await authStore.AddRefreshTokenAsync(refreshToken, ct);

        // Write Audit
        await authStore.WriteAuditAsync(user, "Login", user.Id, now, ct);

        await transaction.CommitAsync(ct);

        return new LoginResponse(
            accessToken.Value,
            refreshTokenStr,
            new AccountResponse(
                user.Id,
                user.Email,
                user.FullName,
                user.Role,
                user.OrganizationId
            )
        );
    }
}


