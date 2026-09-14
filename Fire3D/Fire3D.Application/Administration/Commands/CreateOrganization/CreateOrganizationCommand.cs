using Fire3D.Application.Authentication;
using MediatR;

namespace Fire3D.Application.Administration.Commands.CreateOrganization;

public sealed record CreateOrganizationCommand(Guid ActorId, CreateOrganizationRequest Request, Guid CorrelationId)
    : IRequest<AuthResult<OrganizationResponse>>;
