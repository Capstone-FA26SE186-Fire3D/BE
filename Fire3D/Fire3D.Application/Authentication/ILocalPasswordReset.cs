namespace Fire3D.Application.Authentication;

public interface ILocalPasswordReset
{
    Task<string?> CreateLinkAsync(string email, CancellationToken ct);
    Task<bool> ResetAsync(string token, string newPassword, CancellationToken ct);
}
