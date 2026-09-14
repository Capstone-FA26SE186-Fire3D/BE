using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.CreateAccount;

internal sealed class CreateAccountCommandHandler(IAuthStore store, IPasswordService passwords, TimeProvider clock)
    : IRequestHandler<CreateAccountCommand, AuthResult<AccountResponse>>
{
    public async Task<AuthResult<AccountResponse>> Handle(CreateAccountCommand command, CancellationToken ct)
    {
        var actorId = command.ActorId;
        var request = command.Request;
        var actor = await store.FindUserAsync(actorId, ct);
        if (actor is null || !await AuthSupport.IsActiveAsync(store, actor, ct) || actor.Role != UserRole.PlatformAdmin)
            return AuthResult<AccountResponse>.Fail("FORBIDDEN", "PlatformAdmin is required.", 403);
        return await AuthSupport.CreateAsync(store, passwords, clock, request, actor, ct);
    }
}
