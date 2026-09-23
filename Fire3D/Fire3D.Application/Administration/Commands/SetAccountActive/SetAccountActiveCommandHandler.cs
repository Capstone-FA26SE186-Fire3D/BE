using Fire3D.Application.Administration.Internal;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Internal;
using MediatR;

namespace Fire3D.Application.Administration.Commands.SetAccountActive;

internal sealed class SetAccountActiveCommandHandler(IAdministrationStore store, IAuthStore auth, TimeProvider clock)
    : IRequestHandler<SetAccountActiveCommand, AuthResult<ManagedAccountResponse>>
{
    public async Task<AuthResult<ManagedAccountResponse>> Handle(SetAccountActiveCommand command, CancellationToken ct)
    {
        await using var transaction = await store.BeginManagementTransactionAsync(ct);
        if (!await AdministrationSupport.IsAdminAsync(auth, command.ActorId, ct))
            return AdministrationSupport.Forbidden<ManagedAccountResponse>();
        if (command.IsActive is not bool active)
            return AdministrationSupport.Invalid<ManagedAccountResponse>("isActive is required.");
        if (!active && command.Id == command.ActorId)
            return AuthResult<ManagedAccountResponse>.Fail("SELF_DEACTIVATION", "Administrators cannot deactivate their own account.", 409);
        var target = await auth.FindUserAsync(command.Id, ct);
        if (target is null || target.DeletedAt.HasValue)
            return AdministrationSupport.NotFound<ManagedAccountResponse>();
        if (!active && target.Role == Fire3D.Domain.Enums.UserRole.PlatformAdmin
            && await auth.CountActiveAdminsAsync(ct) <= 1)
            return AuthResult<ManagedAccountResponse>.Fail("LAST_ADMIN", "The last active PlatformAdmin cannot be deactivated.", 409);
        if (target.IsActive != active)
        {
            var now = AuthSupport.UtcNow(clock);
            await store.SetAccountActiveAsync(target.Id, active, now, ct);
            await store.WriteAuditAsync(command.ActorId, target.OrganizationId, "users",
                target.Id, target.IsActive, active, command.CorrelationId, now, ct);
            target = (await auth.FindUserAsync(command.Id, ct))!;
        }
        await transaction.CommitAsync(ct);
        return AuthResult<ManagedAccountResponse>.Ok(AdministrationSupport.ToResponse(target));
    }
}
