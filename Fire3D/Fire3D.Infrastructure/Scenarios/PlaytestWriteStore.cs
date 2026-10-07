using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios.Commands.PreparePlaytestSession;
using Fire3D.Infrastructure.Persistence;
namespace Fire3D.Infrastructure.Scenarios;
/// <summary>Legacy signature lacks live family/idempotency proof. Runtime uses IPlaytestLifecycle.</summary>
public sealed class PlaytestWriteStore:IPlaytestWriteStore
{
 public PlaytestWriteStore(Fire3DDbContext db){_ = db;}
 public Task<AuthResult<Guid>> PreparePlaytestAsync(Guid actorId,Guid buildingId,Guid scenarioId,Guid organizationId,PreparePlaytestRequest request,CancellationToken ct)=>Task.FromResult(AuthResult<Guid>.Fail("PLAYTEST_SESSION_PROOF_REQUIRED","Use the session-bound playtest lifecycle gate.",503));
 public Task<AuthResult<bool>> StartPlaytestAsync(Guid actorId,Guid playtestId,Guid organizationId,CancellationToken ct)=>Task.FromResult(AuthResult<bool>.Fail("PLAYTEST_SESSION_PROOF_REQUIRED","Use the session-bound playtest lifecycle gate.",503));
}
