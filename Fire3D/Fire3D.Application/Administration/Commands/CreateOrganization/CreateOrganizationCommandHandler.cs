using System.Text.RegularExpressions;
using Fire3D.Application.Administration.Internal;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Entities;
using MediatR;

namespace Fire3D.Application.Administration.Commands.CreateOrganization;

internal sealed class CreateOrganizationCommandHandler(IAdministrationStore store, IAuthStore auth, TimeProvider clock)
    : IRequestHandler<CreateOrganizationCommand, AuthResult<OrganizationResponse>>
{
    public async Task<AuthResult<OrganizationResponse>> Handle(CreateOrganizationCommand command, CancellationToken ct)
    {
        await using var transaction = await store.BeginManagementTransactionAsync(ct);
        if (!await AdministrationSupport.IsAdminAsync(auth, command.ActorId, ct))
            return AdministrationSupport.Forbidden<OrganizationResponse>();
        var name = command.Request.Name?.Trim();
        var slug = command.Request.Slug?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name) || name.Length > 200 || string.IsNullOrEmpty(slug) || slug.Length > 100
            || !Regex.IsMatch(slug, "^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            return AdministrationSupport.Invalid<OrganizationResponse>("Name is required (max 200); slug must use lowercase letters, digits and single hyphens (max 100).");
        var now = AuthSupport.UtcNow(clock);
        var organization = new Organization
        {
            Id = Guid.NewGuid(), Name = name, Slug = slug, Plan = "free", Metadata = "{}",
            IsActive = true, CreatedAt = now, UpdatedAt = now
        };
        if (!await store.TryCreateOrganizationAsync(organization, ct))
            return AuthResult<OrganizationResponse>.Fail("SLUG_EXISTS", "Organization slug is already in use.", 409);
        await store.WriteAuditAsync(command.ActorId, organization.Id, "organizations", organization.Id,
            null, true, command.CorrelationId, now, ct);
        await transaction.CommitAsync(ct);
        return AuthResult<OrganizationResponse>.Ok(AdministrationSupport.ToResponse(organization));
    }
}
