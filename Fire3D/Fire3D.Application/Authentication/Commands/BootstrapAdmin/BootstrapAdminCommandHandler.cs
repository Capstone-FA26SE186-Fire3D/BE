using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;
using MediatR;

namespace Fire3D.Application.Authentication.Commands.BootstrapAdmin;

internal sealed class BootstrapAdminCommandHandler(IAuthStore store, IPasswordService passwords, TimeProvider clock)
    : IRequestHandler<BootstrapAdminCommand, AuthResult<AccountResponse>>
{
    public async Task<AuthResult<AccountResponse>> Handle(BootstrapAdminCommand command, CancellationToken ct)
    {
        var email = command.Email;
        var password = command.Password;
        await using var transaction = await store.BeginUserTransactionAsync(Guid.Empty, ct);
        if (await store.HasAdminAsync(ct))
            return AuthResult<AccountResponse>.Fail("ADMIN_EXISTS", "An administrator already exists.", 409);
        var result = await AuthSupport.CreateAsync(store, passwords, clock, new(email, password, "Platform Admin", UserRole.PlatformAdmin, null), null, ct);
        if (result.IsSuccess) await transaction.CommitAsync(ct);
        return result;
    }
}
