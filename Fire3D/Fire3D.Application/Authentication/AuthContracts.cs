using Fire3D.Domain.Enums;

namespace Fire3D.Application.Authentication;

public sealed record LoginRequest(string Email, string Password);
public sealed record RegisterRequest(string Email, string Password, string? FullName, string OrganizationName);
public sealed record RefreshRequest(string RefreshToken);
public sealed record CreateAccountRequest(string Email, string Password, string? FullName,
    UserRole? Role, Guid? OrganizationId);
public sealed record AccountResponse(Guid Id, string Email, string? FullName,
    UserRole Role, Guid? OrganizationId, DateOnly? Dob = null, UserGender? Gender = null,
    string? PhoneNumber = null, string? AvatarUrl = null, bool IsActive = true,
    DateTime? LastLoginAt = null, DateTime? CreatedAt = null, DateTime? UpdatedAt = null);
public sealed record LoginResponse(string AccessToken, string RefreshToken, AccountResponse User);
public sealed record TokenResponse(string AccessToken, DateTime AccessTokenExpiresAt,
    string RefreshToken, DateTime RefreshTokenExpiresAt, AccountResponse User);
public sealed record ForgotPasswordRequest(string Email);
public sealed record ResetPasswordRequest(Guid Token, string NewPassword);
public sealed record AuthError(string Code, string Message, int Status);
public sealed record AuthResult<T>(T? Value, AuthError? Error)
{
    public bool IsSuccess => Error is null;
    public static AuthResult<T> Ok(T value) => new(value, null);
    public static AuthResult<T> Fail(string code, string message, int status) =>
        new(default, new(code, message, status));
}
