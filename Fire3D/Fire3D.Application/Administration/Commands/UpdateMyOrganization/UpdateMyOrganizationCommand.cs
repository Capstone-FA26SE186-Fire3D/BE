using Fire3D.Application.Administration.Internal;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.RegisterUser;
using Fire3D.Application.Authentication.Commands.SelfRegistration;
using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;
using MediatR;

namespace Fire3D.Application.Administration.Commands.UpdateMyOrganization;

public sealed record UpdateMyOrganizationCommand(Guid ActorId, string? IfMatch, UpdateOrganizationProfileRequest Request)
    : IRequest<AuthResult<OrganizationProfileResponse>>;

public sealed class UpdateMyOrganizationCommandHandler(IAuthStore accounts, IAdministrationStore organizations, TimeProvider clock)
    : IRequestHandler<UpdateMyOrganizationCommand, AuthResult<OrganizationProfileResponse>>
{
    public async Task<AuthResult<OrganizationProfileResponse>> Handle(UpdateMyOrganizationCommand command, CancellationToken ct)
    {
        if (OrganizationProfileEdit.Precheck<OrganizationProfileResponse>(command.IfMatch, command.Request, out var revision) is { } rejected)
            return rejected;

        await using var transaction = await organizations.BeginManagementTransactionAsync(ct);
        var actor = await accounts.FindUserAsync(command.ActorId, ct);
        if (actor is null || !await AuthSupport.IsActiveAsync(accounts, actor, ct) || actor.Role != UserRole.OrganizationUser || actor.OrganizationId is not Guid organizationId)
            return AuthResult<OrganizationProfileResponse>.Fail("FORBIDDEN", "Only an active organization user may update this profile.", 403);
        var organization = await organizations.FindOrganizationAsync(organizationId, ct);
        if (organization is null || !organization.IsActive || organization.DeletedAt.HasValue)
            return AuthResult<OrganizationProfileResponse>.Fail("UNAUTHORIZED", "Organization is unavailable.", 401);
        if (OrganizationProfileEdit.Resolve(organization, command.Request) is not var (name, address, phone))
            return OrganizationProfileEdit.Invalid<OrganizationProfileResponse>();
        var now = AuthSupport.UtcNow(clock);
        var result = await organizations.UpdateOrganizationProfileAsync(organizationId, revision, name, address, phone, now, ct);
        if (result == OrganizationProfileUpdateResult.PhoneTaken) return OrganizationProfileEdit.PhoneTaken<OrganizationProfileResponse>();
        if (result == OrganizationProfileUpdateResult.PreconditionFailed) return OrganizationProfileEdit.Stale<OrganizationProfileResponse>();
        if (result == OrganizationProfileUpdateResult.Unavailable)
            return AuthResult<OrganizationProfileResponse>.Fail("UNAUTHORIZED", "Organization is unavailable.", 401);
        await organizations.WriteOrganizationProfileAuditAsync(new(actor.Id, organizationId, Guid.NewGuid(),
            organization.Name, organization.Address, organization.PhoneNumber, name, address, phone, now), ct);
        await transaction.CommitAsync(ct);
        return AuthResult<OrganizationProfileResponse>.Ok(new(organizationId, name, organization.Slug, address, phone, true, revision + 1, organization.CreatedAt, now));
    }
}
