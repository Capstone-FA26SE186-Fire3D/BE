using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Application.Authentication.Services;
using MediatR;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fire3D.Application.Authentication.Commands.LoginWithPassword;

public sealed record LoginWithPasswordCommand(string Email, string Password) : IRequest<AuthResult<LoginResponse>>;

public sealed class LoginWithPasswordCommandHandler(
    IIdentityProvider identityProvider,
    IAuthStore authStore,
    Fire3DSessionIssuer sessionIssuer) : IRequestHandler<LoginWithPasswordCommand, AuthResult<LoginResponse>>
{
    public async Task<AuthResult<LoginResponse>> Handle(LoginWithPasswordCommand request, CancellationToken ct)
    {
                VerifiedIdentity identity;
        try {
            identity = await identityProvider.SignInWithPasswordAsync(request.Email, request.Password, ct);
        } catch (Exception ex) {
            return AuthResult<LoginResponse>.Fail("INVALID_CREDENTIALS", ex.Message, 401);
        }

        var user = await authStore.FindUserByFirebaseUidAsync(identity.Uid, ct);
        if (user == null)
        {
            return AuthResult<LoginResponse>.Fail("USER_NOT_FOUND", "User not found in local database.", 404);
        }

        return AuthResult<LoginResponse>.Ok(await sessionIssuer.IssueSessionAsync(user, ct));
    }
}



