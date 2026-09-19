using System.Text.RegularExpressions;
using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.Register;

internal sealed class RegisterCommandHandler(
    IAuthStore store,
    IPasswordService passwords,
    ITokenService tokens,
    TimeProvider clock)
    : IRequestHandler<RegisterCommand, AuthResult<TokenResponse>>
{
    public async Task<AuthResult<TokenResponse>> Handle(RegisterCommand command, CancellationToken ct)
    {
        var request = command.Request;
        var email = AuthSupport.NormalizeEmail(request.Email);
        
        if (email is null || request.Password is null || request.Password.Length is < 12 or > 128
            || string.IsNullOrWhiteSpace(request.Password) || request.FullName?.Length > 200
            || string.IsNullOrWhiteSpace(request.OrganizationName) || request.OrganizationName.Length > 200)
        {
            return AuthResult<TokenResponse>.Fail("VALIDATION_ERROR", "Invalid email, password (12-128 chars), name, or organization name.", 400);
        }

        var now = AuthSupport.UtcNow(clock);

        // Auto-generate a safe, unique slug from the organization name
        var slugBase = Regex.Replace(request.OrganizationName.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrEmpty(slugBase)) slugBase = "org";
        var slug = $"{slugBase}-{Guid.NewGuid().ToString("N")[..6]}";
        if (slug.Length > 100) slug = slug[..100].Trim('-');

        var orgId = Guid.NewGuid();
        var organization = new Organization
        {
            Id = orgId,
            Name = request.OrganizationName.Trim(),
            Slug = slug,
            Plan = "free",
            Metadata = "{}",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Email = email,
            FullName = request.FullName?.Trim(),
            Role = UserRole.OrganizationUser,
            OrganizationId = orgId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        user.PasswordHash = passwords.Hash(user, request.Password);

        // Atomically create both
        var conflict = await store.TryCreateOrganizationWithUserAsync(organization, user, ct);
        if (conflict == RegisterConflict.EmailTaken)
            return AuthResult<TokenResponse>.Fail("EMAIL_EXISTS", "Email is already registered.", 409);
        if (conflict == RegisterConflict.SlugTaken)
            return AuthResult<TokenResponse>.Fail("SLUG_EXISTS", "Failed to generate unique organization slug. Please try again.", 409);

        // Create audit logs and issue tokens for auto-login
        await store.WriteAuditAsync(user, "Create", orgId, now, ct);
        await store.WriteAuditAsync(user, "Create", userId, now, ct);
        await store.UpdateLoginAsync(userId, now, user.PasswordHash, ct);
        
        var response = await AuthSupport.IssueAsync(store, tokens, user, Guid.NewGuid(), now, now.Add(tokens.RefreshTokenLifetime), ct);
        return AuthResult<TokenResponse>.Ok(response);
    }
}
