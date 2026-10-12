using Fire3D.Application.Administration.Internal;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Internal;
using MediatR;

namespace Fire3D.Application.Administration.Commands.UpdateOrganization;

/// <summary>PlatformAdmin edits an organization's name, address or phone with If-Match. Inactive organizations stay editable; deleted ones are 404.</summary>
public sealed record UpdateOrganizationCommand(Guid ActorId, Guid Id, string? IfMatch, UpdateOrganizationProfileRequest Request, Guid CorrelationId)
    : IRequest<AuthResult<OrganizationResponse>>;

internal sealed class UpdateOrganizationCommandHandler(IAuthStore auth, IAdministrationStore store, TimeProvider clock)
    : IRequestHandler<UpdateOrganizationCommand, AuthResult<OrganizationResponse>>
{
    public async Task<AuthResult<OrganizationResponse>> Handle(UpdateOrganizationCommand command, CancellationToken ct)
    {
        if (OrganizationProfileEdit.Precheck<OrganizationResponse>(command.IfMatch, command.Request, out var revision) is { } rejected)
            return rejected;
        await using var transaction = await store.BeginManagementTransactionAsync(ct);
        if (!await AdministrationSupport.IsAdminAsync(auth, command.ActorId, ct))
            return AdministrationSupport.Forbidden<OrganizationResponse>();
        var organization = await store.FindOrganizationAsync(command.Id, ct);
        if (organization is null || organization.DeletedAt.HasValue)
            return AdministrationSupport.NotFound<OrganizationResponse>();
        if (OrganizationProfileEdit.Resolve(organization, command.Request) is not var (name, address, phone))
            return OrganizationProfileEdit.Invalid<OrganizationResponse>();
        var now = AuthSupport.UtcNow(clock);
        var result = await store.UpdateOrganizationProfileAsync(organization.Id, revision, name, address, phone, now, ct, allowInactive: true);
        if (result == OrganizationProfileUpdateResult.PhoneTaken) return OrganizationProfileEdit.PhoneTaken<OrganizationResponse>();
        if (result == OrganizationProfileUpdateResult.PreconditionFailed) return OrganizationProfileEdit.Stale<OrganizationResponse>();
        if (result == OrganizationProfileUpdateResult.Unavailable) return AdministrationSupport.NotFound<OrganizationResponse>();
        await store.WriteOrganizationProfileAuditAsync(new(command.ActorId, organization.Id, command.CorrelationId,
            organization.Name, organization.Address, organization.PhoneNumber, name, address, phone, now), ct);
        var view = await store.GetOrganizationViewAsync(organization.Id, ct);
        await transaction.CommitAsync(ct);
        return AuthResult<OrganizationResponse>.Ok(view!);
    }
}
