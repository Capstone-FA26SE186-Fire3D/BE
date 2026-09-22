using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.ForgotPassword;

public sealed record ForgotPasswordCommand(string Email) : IRequest;

public sealed class ForgotPasswordCommandHandler(IAuthStore authStore) : IRequestHandler<ForgotPasswordCommand>
{
    public async Task Handle(ForgotPasswordCommand request, CancellationToken ct)
    {
        var normalizedEmail = request.Email.Trim().ToLowerInvariant();
        await authStore.EnqueuePasswordResetAsync(normalizedEmail, ct);
    }
}
