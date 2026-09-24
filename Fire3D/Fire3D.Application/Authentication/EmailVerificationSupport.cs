using MediatR;

namespace Fire3D.Application.Authentication;

public sealed record VerificationEmailJob(Guid Id, string Email, Guid LeaseToken, int Attempt);

public interface IEmailVerificationQueue
{
    Task EnqueueAsync(string email, CancellationToken ct);
    Task<VerificationEmailJob?> ClaimAsync(CancellationToken ct);
    Task<string?> CreateLinkAsync(VerificationEmailJob job, CancellationToken ct);
    Task CompleteAsync(VerificationEmailJob job, CancellationToken ct);
    Task FailAsync(VerificationEmailJob job, bool permanent, CancellationToken ct);
    Task<bool> VerifyAsync(string token, CancellationToken ct);
}

public sealed record ResendVerificationCommand(string Email) : IRequest<AuthResult<bool>>;
public sealed class ResendVerificationCommandHandler(IEmailVerificationQueue queue)
    : IRequestHandler<ResendVerificationCommand, AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(ResendVerificationCommand request, CancellationToken ct)
    {
        var email = PasswordResetValidation.NormalizeEmail(request.Email);
        if (email is null) return AuthResult<bool>.Fail("INVALID_EMAIL", "Use a valid email address (maximum 254 characters).", 400);
        await queue.EnqueueAsync(email, ct);
        return AuthResult<bool>.Ok(true);
    }
}

public sealed record VerifyEmailCommand(string Token) : IRequest<AuthResult<bool>>;
public sealed class VerifyEmailCommandHandler(IEmailVerificationQueue queue)
    : IRequestHandler<VerifyEmailCommand, AuthResult<bool>>
{
    public async Task<AuthResult<bool>> Handle(VerifyEmailCommand request, CancellationToken ct)
    {
        if (!await queue.VerifyAsync(request.Token, ct))
            return AuthResult<bool>.Fail("INVALID_VERIFICATION_TOKEN", "The verification link is invalid, expired or already used.", 400);
        return AuthResult<bool>.Ok(true);
    }
}
