using Fire3D.Domain.Entities;

namespace Fire3D.Application.Authentication;

public interface IAuthTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
}

public interface IAuthStore
{
    Task<IAuthTransaction> BeginUserTransactionAsync(Guid userId, CancellationToken ct);
    Task<User?> FindUserAsync(Guid id, CancellationToken ct);
    Task<User?> FindUserByEmailAsync(string email, CancellationToken ct);
    Task<bool> HasAdminAsync(CancellationToken ct);
    Task<bool> OrganizationIsActiveAsync(Guid id, CancellationToken ct);
    Task<bool> TryCreateUserAsync(User user, CancellationToken ct);
    Task UpdateLoginAsync(Guid id, DateTime now, CancellationToken ct);
    Task<RefreshToken?> FindRefreshTokenAsync(string hash, CancellationToken ct);
    Task AddRefreshTokenAsync(RefreshToken token, CancellationToken ct);
    Task ConsumeRefreshTokenAsync(Guid id, DateTime now, CancellationToken ct);
    Task RevokeFamilyAsync(Guid userId, Guid familyId, DateTime now, CancellationToken ct);
    Task<bool> FamilyIsActiveAsync(Guid userId, Guid familyId, DateTime now, CancellationToken ct);
    Task WriteAuditAsync(User actor, string action, Guid targetId, DateTime now, CancellationToken ct, Guid? correlationId = null);

    // --- Password Reset ---
    Task SavePasswordResetTokenAsync(PasswordResetToken token, CancellationToken ct);
    Task<PasswordResetToken?> FindValidResetTokenAsync(Guid tokenId, CancellationToken ct);
    Task MarkResetTokenUsedAsync(Guid tokenId, DateTime now, CancellationToken ct);
    Task InvalidateUserResetTokensAsync(Guid userId, CancellationToken ct);

    // --- Registration ---
    Task<RegisterConflict> TryCreateOrganizationWithUserAsync(Organization organization, User user, CancellationToken ct);
}

public enum RegisterConflict { None, SlugTaken, EmailTaken }

public interface IPasswordService
{
    string Hash(User user, string password);
    bool Verify(User user, string password, out bool needsRehash);
    void VerifyDummy(string password);
}

public sealed record AccessTokenValue(string Value, DateTime ExpiresAt);
public interface ITokenService
{
    AccessTokenValue CreateAccessToken(User user, Guid familyId, DateTime now);
    string CreateRefreshToken();
    string HashRefreshToken(string token);
    TimeSpan RefreshTokenLifetime { get; }
}
