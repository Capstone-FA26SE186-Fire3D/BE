using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios.Commands.PreparePlaytestSession;
namespace Fire3D.Application.Scenarios;
public sealed record PlaytestPreparation(Guid Id,Guid ScenarioVersionId,Guid ArtifactId,Guid ValidationRunId,string PackageHash,string ManifestHash,string BuildTarget);
public sealed record StartPlaytestRequest(string RuntimeVersion);
/// <summary>Launch grant (five minutes). Generation increases on each reissue; only the current generation can confirm launch.</summary>
public sealed record PlaytestLaunch(Guid PlaytestId,Guid ActorId,Guid FamilyId,Guid ScenarioVersionId,Guid ArtifactId,Guid ValidationRunId,
 string PackageHash,string ManifestHash,string BuildTarget,string PackageKey,string ManifestKey,string ProtocolVersion,string ManifestSchemaVersion,
 string RuntimeVersion,DateTime IssuedAt,DateTime ExpiresAt,string LaunchGrant="",int Generation=1);
public sealed record PlaytestPackageView(Guid ArtifactId,Guid ManifestArtifactId,Guid ValidationRunId,string PackageHash,string ManifestHash,
 string BuildTarget,string ProtocolVersion,string ManifestSchemaVersion);
public sealed record PlaytestGrantView(int Generation,DateTime IssuedAt,DateTime ExpiresAt);
public sealed record PlaytestRecoveryView(bool CanStart,bool CanReissueGrant,bool CanSync,bool CanCancel);
/// <summary>
/// Status: Created → Launching (start, Trial consumed once) → Running (runtime confirmed) → Completed | Cancelled.
/// LaunchSession tells whether the calling session is the one allowed to start/launch/sync. Family IDs are never returned.
/// </summary>
public sealed record PlaytestStatusResponse(Guid Id,Guid BuildingId,Guid RevisionId,Guid ScenarioVersionId,string Status,DateTime CreatedAt,
 DateTime? StartedAt,DateTime? EndedAt,string? RuntimeVersion,PlaytestPackageView Package,PlaytestGrantView? Grant,DateTime? LaunchedAt,
 DateTime? LastHeartbeatAt,long AcknowledgedSequence,bool BoundToMobile,bool LaunchSession,DateTime? OpenHandoffExpiresAt,
 PlaytestRecoveryView Recovery,JsonElement? Completion);
/// <summary>QR content is DeepLink (configured base URL + code). Code is shown once per key and never logged.</summary>
public sealed record PlaytestHandoffResponse(Guid HandoffId,Guid PlaytestId,string Code,string DeepLink,DateTime ExpiresAt);
public sealed record RedeemPlaytestHandoffRequest(string Code);
public sealed record LaunchedPlaytestRequest(int Generation);
public sealed record PlaytestEvent(Guid EventId,long Sequence,string SchemaVersion,string Type,DateTimeOffset? OccurredAt,JsonElement Payload);
public sealed record PlaytestEventsRequest(IReadOnlyList<PlaytestEvent> Events);
public sealed record PlaytestEventConflict(Guid EventId,string Code);
public sealed record SequenceRange(long From,long To);
/// <summary>AcknowledgedSequence is the highest N with events 1..N stored; MissingRanges lists gaps below the highest received sequence.</summary>
public sealed record PlaytestEventsResponse(IReadOnlyList<Guid> Accepted,IReadOnlyList<Guid> Duplicates,IReadOnlyList<PlaytestEventConflict> Conflicts,
 long AcknowledgedSequence,IReadOnlyList<SequenceRange> MissingRanges);
public sealed record CompletePlaytestRequest(long LastEventSequence,JsonElement? Summary=null);
public sealed record PlaytestHeartbeatResponse(DateTime ServerTime,string Status);
public sealed class PlaytestOptions
{
 public bool Enabled {get;set;}
 public string SigningKey {get;set;}="";
 public string Issuer {get;set;}="FET3D.Playtest";
 public string Audience {get;set;}="FET3D.Runtime.Playtest";
 /// <summary>Base64 32-byte AES key used only to replay a lost handoff create response within its five-minute TTL.</summary>
 public string HandoffKey {get;set;}="";
 /// <summary>Deep-link base such as fet3d://playtest/handoff; the QR contains only this URL with the code.</summary>
 public string HandoffDeepLinkBaseUrl {get;set;}="";
 public bool HandoffEnabled=>Convert.TryFromBase64String(HandoffKey,new byte[32],out var bytes) && bytes==32
  && Uri.TryCreate(HandoffDeepLinkBaseUrl,UriKind.Absolute,out var link) && string.IsNullOrEmpty(link.Query) && string.IsNullOrEmpty(link.Fragment);
}
public interface IPlaytestLifecycle
{
 Task<AuthResult<PlaytestPreparation>> PrepareAsync(Guid actor,Guid family,Guid? building,Guid scenario,PreparePlaytestRequest request,string? key,CancellationToken ct);
 Task<AuthResult<PlaytestLaunch>> StartAsync(Guid actor,Guid family,Guid playtest,StartPlaytestRequest request,string? key,CancellationToken ct);
 Task<AuthResult<PlaytestStatusResponse>> GetAsync(Guid actor,Guid family,Guid playtest,CancellationToken ct)=>throw new NotSupportedException();
 Task<AuthResult<PlaytestHandoffResponse>> CreateHandoffAsync(Guid actor,Guid family,Guid playtest,string? key,CancellationToken ct)=>throw new NotSupportedException();
 Task<AuthResult<PlaytestStatusResponse>> RedeemHandoffAsync(Guid actor,Guid family,RedeemPlaytestHandoffRequest request,CancellationToken ct)=>throw new NotSupportedException();
 Task<AuthResult<PlaytestLaunch>> ReissueGrantAsync(Guid actor,Guid family,Guid playtest,string? key,CancellationToken ct)=>throw new NotSupportedException();
 Task<AuthResult<PlaytestStatusResponse>> LaunchedAsync(Guid actor,Guid family,Guid playtest,LaunchedPlaytestRequest request,CancellationToken ct)=>throw new NotSupportedException();
 Task<AuthResult<PlaytestHeartbeatResponse>> HeartbeatAsync(Guid actor,Guid family,Guid playtest,CancellationToken ct)=>throw new NotSupportedException();
 Task<AuthResult<PlaytestEventsResponse>> RecordEventsAsync(Guid actor,Guid family,Guid playtest,PlaytestEventsRequest request,CancellationToken ct)=>throw new NotSupportedException();
 Task<AuthResult<PlaytestStatusResponse>> CompleteAsync(Guid actor,Guid family,Guid playtest,CompletePlaytestRequest request,string? key,CancellationToken ct)=>throw new NotSupportedException();
 Task<AuthResult<PlaytestStatusResponse>> CancelAsync(Guid actor,Guid family,Guid playtest,string? key,CancellationToken ct)=>throw new NotSupportedException();
}
