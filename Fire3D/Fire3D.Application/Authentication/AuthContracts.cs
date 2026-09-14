using Fire3D.Domain.Enums;

namespace Fire3D.Application.Authentication;

public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record CreateAccountRequest(string Email, string Password, string? FullName,
    UserRole? Role, Guid? OrganizationId);
public sealed record AccountResponse(Guid Id, string Email, string? FullName,
    UserRole Role, Guid? OrganizationId);
public sealed record TokenResponse(string AccessToken, DateTime AccessTokenExpiresAt,
    string RefreshToken, DateTime RefreshTokenExpiresAt, AccountResponse User);
public sealed record AuthError(string Code, string Message, int Status);
public sealed record AuthResult<T>(T? Value, AuthError? Error)
{
    public bool IsSuccess => Error is null;
    public static AuthResult<T> Ok(T value) => new(value, null);
    public static AuthResult<T> Fail(string code, string message, int status) =>
        new(default, new(code, message, status));
}
