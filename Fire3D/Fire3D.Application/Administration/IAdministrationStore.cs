using Fire3D.Application.Authentication;
using Fire3D.Domain.Entities;

namespace Fire3D.Application.Administration;

public interface IAdministrationStore
{
    Task<IAuthTransaction> BeginManagementTransactionAsync(CancellationToken ct);
    Task<Organization?> FindOrganizationAsync(Guid id, CancellationToken ct);
    Task<bool> TryCreateOrganizationAsync(Organization organization, CancellationToken ct);
    Task<PageResponse<OrganizationResponse>> ListOrganizationsAsync(OrganizationFilter filter, CancellationToken ct);
    Task<PageResponse<ManagedAccountResponse>> ListAccountsAsync(AccountFilter filter, CancellationToken ct);
    Task SetOrganizationActiveAsync(Guid id, bool active, DateTime now, CancellationToken ct);
    Task SetAccountActiveAsync(Guid id, bool active, DateTime now, CancellationToken ct);
    Task WriteAuditAsync(Guid actorId, Guid? organizationId, string targetEntity, Guid targetId,
        bool? previousActive, bool active, Guid correlationId, DateTime now, CancellationToken ct);
}
