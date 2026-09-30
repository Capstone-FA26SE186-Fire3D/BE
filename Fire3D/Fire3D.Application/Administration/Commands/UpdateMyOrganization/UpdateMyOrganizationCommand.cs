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
        if (!ProfileEtag.TryParse(command.IfMatch, out var revision))
            return AuthResult<OrganizationProfileResponse>.Fail(string.IsNullOrWhiteSpace(command.IfMatch) ? "PRECONDITION_REQUIRED" : "VALIDATION_ERROR", "Send the organization ETag in If-Match.", string.IsNullOrWhiteSpace(command.IfMatch) ? 428 : 400);
        if (command.Request is null)
            return AuthResult<OrganizationProfileResponse>.Fail("VALIDATION_ERROR", "Organization profile request is required.", 400);
        if (!command.Request.NameSpecified && !command.Request.AddressSpecified && !command.Request.PhoneNumberSpecified)
            return AuthResult<OrganizationProfileResponse>.Fail("VALIDATION_ERROR", "Send at least one organization profile field.", 400);

        await using var transaction = await organizations.BeginManagementTransactionAsync(ct);
        var actor = await accounts.FindUserAsync(command.ActorId, ct);
        if (actor is null || !await AuthSupport.IsActiveAsync(accounts, actor, ct) || actor.Role != UserRole.OrganizationUser || actor.OrganizationId is not Guid organizationId)
            return AuthResult<OrganizationProfileResponse>.Fail("FORBIDDEN", "Only an active organization user may update this profile.", 403);
        var organization = await organizations.FindOrganizationAsync(organizationId, ct);
        if (organization is null || !organization.IsActive || organization.DeletedAt.HasValue)
            return AuthResult<OrganizationProfileResponse>.Fail("UNAUTHORIZED", "Organization is unavailable.", 401);
        var name = command.Request.NameSpecified ? command.Request.Name?.Trim() : organization.Name;
        var address = command.Request.AddressSpecified ? command.Request.Address?.Trim() : organization.Address;
        var phone = command.Request.PhoneNumberSpecified
            ? command.Request.PhoneNumber is null ? null : SelfRegistrationValidation.NormalizePhone(command.Request.PhoneNumber)
            : organization.PhoneNumber;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || (command.Request.AddressSpecified && command.Request.Address is not null && string.IsNullOrWhiteSpace(address)) || address?.Length > 2000 || (command.Request.PhoneNumberSpecified && command.Request.PhoneNumber is not null && phone is null))
            return AuthResult<OrganizationProfileResponse>.Fail("VALIDATION_ERROR", "Name, address, or phone number is invalid.", 400);
        var now = AuthSupport.UtcNow(clock);
        var result = await organizations.UpdateOrganizationProfileAsync(organizationId, revision, name, address, phone, now, ct);
        if (result == OrganizationProfileUpdateResult.PreconditionFailed)
            return AuthResult<OrganizationProfileResponse>.Fail("PRECONDITION_FAILED", "The organization profile changed. Reload it and retry.", 412);
        if (result == OrganizationProfileUpdateResult.Unavailable)
            return AuthResult<OrganizationProfileResponse>.Fail("UNAUTHORIZED", "Organization is unavailable.", 401);
        await organizations.WriteAuditAsync(actor.Id, organizationId, "organizations", organizationId, organization.IsActive, organization.IsActive, Guid.NewGuid(), now, ct);
        await transaction.CommitAsync(ct);
        return AuthResult<OrganizationProfileResponse>.Ok(new(organizationId, name, organization.Slug, address, phone, true, revision + 1, organization.CreatedAt, now));
    }
}
