
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Entities;
using FirebaseAdmin.Auth;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.FirebaseLogin;

public record ExchangeFirebaseTokenCommand(string IdToken) : IRequest<AuthResult<TokenResponse>>;

public sealed class ExchangeFirebaseTokenCommandHandler(IAuthStore store, ITokenService tokens, TimeProvider clock)
    : IRequestHandler<ExchangeFirebaseTokenCommand, AuthResult<TokenResponse>>
{
    public async Task<AuthResult<TokenResponse>> Handle(ExchangeFirebaseTokenCommand request, CancellationToken ct)
    {
        var authenticatedAt = AuthSupport.UtcNow(clock);
        FirebaseToken decodedToken;
        try
        {
            decodedToken = await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(request.IdToken, true, ct);
        }
        catch (Exception)
        {
            return AuthResult<TokenResponse>.Fail("INVALID_FIREBASE_TOKEN", "Token Firebase không hợp lệ hoặc đã hết hạn.", 401);
        }

        var uid = decodedToken.Uid;
        var email = decodedToken.Claims.TryGetValue("email", out var emailObj) ? emailObj?.ToString() : null;

        if (string.IsNullOrEmpty(email))
        {
            return AuthResult<TokenResponse>.Fail("INVALID_FIREBASE_TOKEN", "Tài khoản Firebase không có email.", 400);
        }

        // Lấy hoặc tạo user
        var user = await store.FindUserByFirebaseUidAsync(uid, ct) ?? await store.FindUserByEmailAsync(email, ct);
        
        var now = authenticatedAt;
        
        if (user == null)
        {
            // Tự động cấp quyền (nếu cần thiết theo business logic). 
            // Tạm thời coi đây là chức năng tự động đăng ký:
            user = new User
            {
                Id = Guid.NewGuid(),
                FirebaseUid = uid,
                Email = email,
                FullName = decodedToken.Claims.TryGetValue("name", out var nameObj) ? nameObj?.ToString() : "New User",
                Role = Fire3D.Domain.Enums.UserRole.Trainee,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };
            await store.TryCreateUserAsync(user, ct);
        }
        else if (string.IsNullOrEmpty(user.FirebaseUid))
        {
            // C?p nh?t Uid ch? khi ch?a link (được PlatformAdmin cấp)
            user.FirebaseUid = uid;
            await store.UpdateUserAsync(user, ct);
        }
        else if (user.FirebaseUid != uid)
        {
            return AuthResult<TokenResponse>.Fail("UID_MISMATCH", "Email dã được liên kết v?i tài khoản Firebase khác.", 400);
        }

        if (!await AuthSupport.IsActiveAsync(store, user, ct))
        {
            return AuthResult<TokenResponse>.Fail("ACCOUNT_DISABLED", "Tài khoản bị vô hiệu hóa.", 403);
        }

        await store.UpdateLoginAsync(user.Id, now, ct);
        var familyId = Guid.NewGuid();
        var refreshExpiresAt = now.AddDays(7);
        return AuthResult<TokenResponse>.Ok(await AuthSupport.IssueAsync(store, tokens, user, familyId, now, refreshExpiresAt, ct));
    }
}

