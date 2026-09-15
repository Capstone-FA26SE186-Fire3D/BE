using Fire3D.Application.Authentication;
using MediatR;
namespace Fire3D.Application.Administration.Commands.SetAccountActive;
public sealed record SetAccountActiveCommand(Guid ActorId, Guid Id, bool? IsActive, Guid CorrelationId)
    : IRequest<AuthResult<ManagedAccountResponse>>;
