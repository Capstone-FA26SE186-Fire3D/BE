using Fire3D.Application.Administration.Internal;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Internal;
using MediatR;

namespace Fire3D.Application.Administration.Commands.SetOrganizationActive;

internal sealed class SetOrganizationActiveCommandHandler(IAdministrationStore store, IAuthStore auth, TimeProvider clock)
    : IRequestHandler<SetOrganizationActiveCommand, AuthResult<OrganizationResponse>>
{
    public async Task<AuthResult<OrganizationResponse>> Handle(SetOrganizationActiveCommand command, CancellationToken ct)
    {
        await using var transaction = await store.BeginManagementTransactionAsync(ct);
        if (!await AdministrationSupport.IsAdminAsync(auth, command.ActorId, ct))
            return AdministrationSupport.Forbidden<OrganizationResponse>();
        if (command.IsActive is not bool active)
            return AdministrationSupport.Invalid<OrganizationResponse>("isActive is required.");

        var target = await store.FindOrganizationAsync(command.Id, ct);
        if (target is null || target.DeletedAt.HasValue)
            return AdministrationSupport.NotFound<OrganizationResponse>();
        if (target.IsActive != active)
        {
            var now = AuthSupport.UtcNow(clock);
            await store.SetOrganizationActiveAsync(target.Id, active, now, ct);
            await store.WriteAuditAsync(command.ActorId, target.Id, "organizations",
                target.Id, target.IsActive, active, command.CorrelationId, now, ct);
            target = (await store.FindOrganizationAsync(command.Id, ct))!;
        }
        await transaction.CommitAsync(ct);
        return AuthResult<OrganizationResponse>.Ok(AdministrationSupport.ToResponse(target));
    }
}
