using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios.Commands.PreparePlaytestSession;
namespace Fire3D.Application.Scenarios;
public sealed record PlaytestPreparation(Guid Id,Guid ScenarioVersionId,Guid ArtifactId,Guid ValidationRunId,string PackageHash,string ManifestHash,string BuildTarget);
public sealed record StartPlaytestRequest(string RuntimeVersion);
public sealed record PlaytestLaunch(Guid PlaytestId,Guid ActorId,Guid FamilyId,Guid ScenarioVersionId,Guid ArtifactId,Guid ValidationRunId,
 string PackageHash,string ManifestHash,string BuildTarget,string PackageKey,string ManifestKey,string ProtocolVersion,string ManifestSchemaVersion,
 string RuntimeVersion,DateTime IssuedAt,DateTime ExpiresAt,string LaunchGrant="");
public sealed class PlaytestOptions
{
 public bool Enabled {get;set;}
 public string SigningKey {get;set;}="";
 public string Issuer {get;set;}="FET3D.Playtest";
 public string Audience {get;set;}="FET3D.Runtime.Playtest";
}
public interface IPlaytestLifecycle
{
 Task<AuthResult<PlaytestPreparation>> PrepareAsync(Guid actor,Guid family,Guid? building,Guid scenario,PreparePlaytestRequest request,string? key,CancellationToken ct);
 Task<AuthResult<PlaytestLaunch>> StartAsync(Guid actor,Guid family,Guid playtest,StartPlaytestRequest request,string? key,CancellationToken ct);
}
