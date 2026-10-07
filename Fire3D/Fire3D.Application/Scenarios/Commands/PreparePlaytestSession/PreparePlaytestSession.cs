using Fire3D.Application.Authentication;
using MediatR;
namespace Fire3D.Application.Scenarios.Commands.PreparePlaytestSession;
public sealed record PreparePlaytestRequest(Guid RevisionId,Guid? ScenarioDraftId=null,Guid? ScenarioVersionId=null,
 string? PackageHash=null,string? ProtocolVersion=null,string? ManifestSchemaVersion=null,string? RuntimeVersion=null);
public sealed record PreparePlaytestSessionCommand(Guid ActorId,Guid FamilyId,Guid? BuildingId,Guid ScenarioId,PreparePlaytestRequest Request,string? Key):IRequest<AuthResult<PlaytestPreparation>>;
public interface IPlaytestWriteStore
{
    Task<AuthResult<Guid>> PreparePlaytestAsync(Guid actorId, Guid buildingId, Guid scenarioId, Guid organizationId, PreparePlaytestRequest request, CancellationToken ct);
    Task<AuthResult<bool>> StartPlaytestAsync(Guid actorId, Guid playtestId, Guid organizationId, CancellationToken ct);
}


public sealed class PreparePlaytestSessionHandler(IPlaytestLifecycle service):IRequestHandler<PreparePlaytestSessionCommand,AuthResult<PlaytestPreparation>>
{
 public Task<AuthResult<PlaytestPreparation>> Handle(PreparePlaytestSessionCommand command,CancellationToken ct)=>service.PrepareAsync(command.ActorId,command.FamilyId,command.BuildingId,command.ScenarioId,command.Request,command.Key,ct);
}
