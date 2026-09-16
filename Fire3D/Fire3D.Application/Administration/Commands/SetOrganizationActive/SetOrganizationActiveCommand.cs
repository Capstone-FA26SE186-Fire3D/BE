using Fire3D.Application.Authentication;
using MediatR;
namespace Fire3D.Application.Administration.Commands.SetOrganizationActive;
public sealed record SetOrganizationActiveCommand(Guid ActorId, Guid Id, bool? IsActive, Guid CorrelationId)
    : IRequest<AuthResult<OrganizationResponse>>;
