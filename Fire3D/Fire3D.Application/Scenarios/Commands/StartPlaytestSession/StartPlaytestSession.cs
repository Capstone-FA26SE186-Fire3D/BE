using Fire3D.Application.Authentication;
using MediatR;
namespace Fire3D.Application.Scenarios.Commands.StartPlaytestSession;
public sealed record StartPlaytestSessionCommand(Guid ActorId,Guid FamilyId,Guid PlaytestId,StartPlaytestRequest Request,string? Key):IRequest<AuthResult<PlaytestLaunch>>;
public sealed class StartPlaytestSessionHandler(IPlaytestLifecycle service):IRequestHandler<StartPlaytestSessionCommand,AuthResult<PlaytestLaunch>>
{
 public Task<AuthResult<PlaytestLaunch>> Handle(StartPlaytestSessionCommand command,CancellationToken ct)=>service.StartAsync(command.ActorId,command.FamilyId,command.PlaytestId,command.Request,command.Key,ct);
}
