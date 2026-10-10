using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios;
using Fire3D.Application.Scenarios.Commands.PreparePlaytestSession;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using NpgsqlTypes;
namespace Fire3D.Infrastructure.Scenarios;
public sealed class PlaytestLifecycle(Fire3DDbContext db,IOptions<PlaytestOptions> options):IPlaytestLifecycle
{
 private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
 public async Task<AuthResult<PlaytestPreparation>> PrepareAsync(Guid actor,Guid family,Guid? building,Guid scenario,PreparePlaytestRequest request,string? key,CancellationToken ct)
 {
  var errors=new Dictionary<string,string[]>();
  if(request.RevisionId==Guid.Empty)errors["revisionId"]=["A revision UUID is required."];
  if(request.ScenarioDraftId.HasValue==request.ScenarioVersionId.HasValue || request.ScenarioDraftId==Guid.Empty || request.ScenarioVersionId==Guid.Empty)errors["scenarioVersionId"]=["Select exactly one nonempty draft or version UUID."];
  if(request.PackageHash is not null && (request.PackageHash.Length!=64 || !request.PackageHash.All(char.IsAsciiHexDigit)))errors["packageHash"]=["Use a SHA-256 hash or omit legacy metadata."];
  if(request.ProtocolVersion?.Length>50)errors["protocolVersion"]=["Maximum 50 characters."];
  if(request.ManifestSchemaVersion?.Length>50)errors["manifestSchemaVersion"]=["Maximum 50 characters."];
  if(request.RuntimeVersion?.Length>50)errors["runtimeVersion"]=["Maximum 50 characters."];
  if(errors.Count>0)return AuthResult<PlaytestPreparation>.Fail("VALIDATION_ERROR","Playtest validation failed.",400,errors);
  var result=await Execute("Prepare",actor,family,scenario,building,request with{PackageHash=request.PackageHash?.ToLowerInvariant()},key,ct);
  return result.IsSuccess?AuthResult<PlaytestPreparation>.Ok(result.Value.Deserialize<PlaytestPreparation>(Json)!):new(default,result.Error);
 }
 public async Task<AuthResult<PlaytestLaunch>> StartAsync(Guid actor,Guid family,Guid playtest,StartPlaytestRequest request,string? key,CancellationToken ct)
 {
  if(string.IsNullOrEmpty(request.RuntimeVersion) || request.RuntimeVersion.Length>50 || !Regex.IsMatch(request.RuntimeVersion,@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))
   return AuthResult<PlaytestLaunch>.Fail("VALIDATION_ERROR","A canonical major.minor.patch runtime version is required.",400,new Dictionary<string,string[]>{["runtimeVersion"]=["Use major.minor.patch from the active runtime catalog."]});
  // Validate signing material before any Trial mutation; never store raw JWT in the receipt.
  if(Signing() is not { } credentials)return AuthResult<PlaytestLaunch>.Fail("PLAYTEST_LAUNCH_UNAVAILABLE","Playtest launch signing is not configured.",503);
  var result=await Execute("Start",actor,family,playtest,null,request,key,ct);
  return result.IsSuccess?AuthResult<PlaytestLaunch>.Ok(Sign(result.Value.Deserialize<PlaytestLaunch>(Json)!,credentials)):new(default,result.Error);
 }
 public async Task<AuthResult<PlaytestStatusResponse>> GetAsync(Guid actor,Guid family,Guid playtest,CancellationToken ct)=>
  Status(await Runtime("Get",actor,family,playtest,new{},null,ct));
 public async Task<AuthResult<PlaytestHandoffResponse>> CreateHandoffAsync(Guid actor,Guid family,Guid playtest,string? key,CancellationToken ct)
 {
  var config=options.Value;
  if(!config.HandoffEnabled)return AuthResult<PlaytestHandoffResponse>.Fail("PLAYTEST_HANDOFF_UNAVAILABLE","Mobile handoff is not configured.",503);
  var code=Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
  var result=await Runtime("Handoff",actor,family,playtest,new{codeHash=Hash(code),codeCiphertext=Convert.ToBase64String(Encrypt(code,playtest))},key,ct);
  if(!result.IsSuccess)return new(default,result.Error);
  // A replayed key returns the original code, recovered from its short-lived ciphertext; the fresh code above is discarded.
  var stored=Decrypt(Convert.FromBase64String(result.Value.GetProperty("codeCiphertext").GetString()!),playtest);
  return AuthResult<PlaytestHandoffResponse>.Ok(new(result.Value.GetProperty("handoffId").GetGuid(),playtest,stored,
   $"{config.HandoffDeepLinkBaseUrl}?code={Uri.EscapeDataString(stored)}",result.Value.GetProperty("expiresAt").GetDateTimeOffset().UtcDateTime));
 }
 public async Task<AuthResult<PlaytestStatusResponse>> RedeemHandoffAsync(Guid actor,Guid family,RedeemPlaytestHandoffRequest request,CancellationToken ct)
 {
  if(request.Code is not { Length:43 } code || !code.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_'))
   return AuthResult<PlaytestStatusResponse>.Fail("PLAYTEST_HANDOFF_INVALID","Handoff code is not valid.",404);
  return Status(await Runtime("Redeem",actor,family,Guid.Empty,new{codeHash=Hash(code)},null,ct));
 }
 public async Task<AuthResult<PlaytestLaunch>> ReissueGrantAsync(Guid actor,Guid family,Guid playtest,string? key,CancellationToken ct)
 {
  if(Signing() is not { } credentials)return AuthResult<PlaytestLaunch>.Fail("PLAYTEST_LAUNCH_UNAVAILABLE","Playtest launch signing is not configured.",503);
  var result=await Runtime("Reissue",actor,family,playtest,new{},key,ct);
  return result.IsSuccess?AuthResult<PlaytestLaunch>.Ok(Sign(result.Value.Deserialize<PlaytestLaunch>(Json)!,credentials)):new(default,result.Error);
 }
 public async Task<AuthResult<PlaytestStatusResponse>> LaunchedAsync(Guid actor,Guid family,Guid playtest,LaunchedPlaytestRequest request,CancellationToken ct)
 {
  if(request.Generation<1)return AuthResult<PlaytestStatusResponse>.Fail("VALIDATION_ERROR","generation must identify the launch grant.",422);
  return Status(await Runtime("Launched",actor,family,playtest,request,null,ct));
 }
 public async Task<AuthResult<PlaytestHeartbeatResponse>> HeartbeatAsync(Guid actor,Guid family,Guid playtest,CancellationToken ct)
 {
  var result=await Runtime("Heartbeat",actor,family,playtest,new{},null,ct);
  return result.IsSuccess?AuthResult<PlaytestHeartbeatResponse>.Ok(result.Value.GetProperty("result").Deserialize<PlaytestHeartbeatResponse>(Json)!):new(default,result.Error);
 }
 public async Task<AuthResult<PlaytestEventsResponse>> RecordEventsAsync(Guid actor,Guid family,Guid playtest,PlaytestEventsRequest request,CancellationToken ct)
 {
  if(request.Events is not { Count: >=1 and <=500 } events || events.Any(e=>e is null || e.EventId==Guid.Empty || e.Sequence<1 || string.IsNullOrWhiteSpace(e.SchemaVersion) || e.SchemaVersion.Length>32
   || string.IsNullOrWhiteSpace(e.Type) || e.Type.Length>100 || e.Payload.ValueKind!=JsonValueKind.Object) || events.Select(e=>e.EventId).Distinct().Count()!=events.Count)
   return AuthResult<PlaytestEventsResponse>.Fail("VALIDATION_ERROR","Send 1-500 events with unique eventId, sequence >= 1, schemaVersion, type and an object payload.",422);
  var result=await Runtime("Events",actor,family,playtest,new{events=events.Select(e=>new{eventId=e.EventId,sequence=e.Sequence,schemaVersion=e.SchemaVersion,type=e.Type,occurredAt=e.OccurredAt?.ToUniversalTime().ToString("O"),payload=e.Payload})},null,ct);
  return result.IsSuccess?AuthResult<PlaytestEventsResponse>.Ok(result.Value.GetProperty("result").Deserialize<PlaytestEventsResponse>(Json)!):new(default,result.Error);
 }
 public async Task<AuthResult<PlaytestStatusResponse>> CompleteAsync(Guid actor,Guid family,Guid playtest,CompletePlaytestRequest request,string? key,CancellationToken ct)
 {
  if(request.LastEventSequence<0 || request.Summary is { } summary && summary.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
   return AuthResult<PlaytestStatusResponse>.Fail("VALIDATION_ERROR","lastEventSequence must be >= 0 and summary an object.",422);
  object input=request.Summary is { ValueKind: JsonValueKind.Object } s?new{lastEventSequence=request.LastEventSequence,summary=s}:new{lastEventSequence=request.LastEventSequence};
  return Status(await Runtime("Complete",actor,family,playtest,input,key,ct));
 }
 public async Task<AuthResult<PlaytestStatusResponse>> CancelAsync(Guid actor,Guid family,Guid playtest,string? key,CancellationToken ct)=>
  Status(await Runtime("Cancel",actor,family,playtest,new{},key,ct));

 private static AuthResult<PlaytestStatusResponse> Status(AuthResult<JsonElement> result)=>
  result.IsSuccess?AuthResult<PlaytestStatusResponse>.Ok(result.Value.GetProperty("result").Deserialize<PlaytestStatusResponse>(Json)!):new(default,result.Error);
 private SigningCredentials? Signing()
 {
  var config=options.Value;
  if(!config.Enabled)return null;
  var credentials=new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config.SigningKey)),SecurityAlgorithms.HmacSha256);
  if(credentials.Key.KeySize<256)throw new InvalidOperationException("Invalid playtest signing key.");
  return credentials;
 }
 private PlaytestLaunch Sign(PlaytestLaunch launch,SigningCredentials credentials)
 {
  var config=options.Value;
  var claims=new[]{new Claim("purpose","playtest"),new Claim(JwtRegisteredClaimNames.Sub,launch.ActorId.ToString()),new Claim("sid",launch.FamilyId.ToString()),new Claim("playtest_id",launch.PlaytestId.ToString()),new Claim("scenario_version_id",launch.ScenarioVersionId.ToString()),new Claim("artifact_id",launch.ArtifactId.ToString()),new Claim("validation_run_id",launch.ValidationRunId.ToString()),new Claim("package_hash",launch.PackageHash),new Claim("manifest_hash",launch.ManifestHash),new Claim("build_target",launch.BuildTarget),new Claim("runtime_version",launch.RuntimeVersion),new Claim("protocol_version",launch.ProtocolVersion),new Claim("manifest_schema_version",launch.ManifestSchemaVersion),new Claim("grant_generation",launch.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),ClaimValueTypes.Integer32),new Claim(JwtRegisteredClaimNames.Jti,$"{launch.PlaytestId}:{launch.Generation}")};
  var jwt=new JwtSecurityToken(config.Issuer,config.Audience,claims,launch.IssuedAt,launch.ExpiresAt,credentials);
  return launch with{LaunchGrant=new JwtSecurityTokenHandler().WriteToken(jwt)};
 }
 private static string Hash(string code)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
 // AES-GCM with the playtest ID as associated data: a ciphertext cannot be replayed for another playtest.
 private byte[] Encrypt(string code,Guid playtest)
 {
  using var aes=new AesGcm(Convert.FromBase64String(options.Value.HandoffKey),16);
  var nonce=RandomNumberGenerator.GetBytes(12);var plain=Encoding.UTF8.GetBytes(code);var cipher=new byte[plain.Length];var tag=new byte[16];
  aes.Encrypt(nonce,plain,cipher,tag,playtest.ToByteArray());
  return [..nonce,..tag,..cipher];
 }
 private string Decrypt(byte[] value,Guid playtest)
 {
  using var aes=new AesGcm(Convert.FromBase64String(options.Value.HandoffKey),16);
  var plain=new byte[value.Length-28];
  aes.Decrypt(value.AsSpan(0,12),value.AsSpan(28),value.AsSpan(12,16),plain,playtest.ToByteArray());
  return Encoding.UTF8.GetString(plain);
 }
 private Task<AuthResult<JsonElement>> Runtime(string action,Guid actor,Guid family,Guid playtest,object input,string? key,CancellationToken ct)=>
  Gate("SELECT playtest_runtime_gate(@action,@actor,@family,@resource,@input,@key)::text",action,actor,family,playtest,null,input,key,ct);
 private Task<AuthResult<JsonElement>> Execute(string action,Guid actor,Guid family,Guid resource,Guid? building,object input,string? key,CancellationToken ct)=>
  Gate("SELECT playtest_lifecycle_gate(@action,@actor,@family,@resource,@building,@input,@key)::text",action,actor,family,resource,building,input,key,ct);
 private async Task<AuthResult<JsonElement>> Gate(string sql,string action,Guid actor,Guid family,Guid resource,Guid? building,object input,string? key,CancellationToken ct)
 {
  await db.Database.OpenConnectionAsync(ct);
  await using var command=new NpgsqlCommand(sql,(NpgsqlConnection)db.Database.GetDbConnection());
  command.Parameters.AddWithValue("action",action);command.Parameters.AddWithValue("actor",actor);command.Parameters.AddWithValue("family",family);command.Parameters.AddWithValue("resource",resource);
  if(sql.Contains("@building"))command.Parameters.AddWithValue("building",NpgsqlDbType.Uuid,(object?)building??DBNull.Value);
  command.Parameters.AddWithValue("input",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(input,Json));command.Parameters.AddWithValue("key",NpgsqlDbType.Text,(object?)key??DBNull.Value);
  using var document=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);var value=document.RootElement;
  if(value.GetProperty("code").GetString()=="OK")return AuthResult<JsonElement>.Ok(value.Clone());
  var errors=value.TryGetProperty("errors",out var fields)?fields.Deserialize<Dictionary<string,string[]>>():null;
  var code=value.GetProperty("code").GetString()!;
  var message=code switch{"RUNTIME_INCOMPATIBLE"=>"Runtime is not active or does not support this package's version, protocol, schema and capabilities.","PLAYTEST_ENTITLEMENT_REQUIRED"=>"This Building needs a current paid service entitlement or remaining Trial playtest units.","DRAFT_SNAPSHOT_REQUIRED"=>"Snapshot the current draft before requesting its package build.","PLAYTEST_PACKAGE_REQUIRED"=>"Build and validate a PlaytestPackage for this exact immutable version first.","PLAYTEST_GRANT_EXPIRED"=>"The launch grant has expired; it cannot be renewed by replay.",
   "PLAYTEST_GRANT_STALE"=>"A newer launch grant generation exists; use the current grant.","PLAYTEST_HANDOFF_EXPIRED"=>"The handoff code expired or was replaced; create a new code.","PLAYTEST_HANDOFF_USED"=>"The handoff code was already redeemed by another session.","PLAYTEST_HANDOFF_REVOKED"=>"The handoff code was replaced or cancelled.","PLAYTEST_HANDOFF_ISSUER_INVALID"=>"The Web session that issued the code is no longer valid.","PLAYTEST_HANDOFF_INVALID"=>"Handoff code is not valid.","PLAYTEST_TERMINAL"=>"The playtest already ended.","PLAYTEST_EVENTS_INCOMPLETE"=>"Send every event up to lastEventSequence before completing.","PLAYTEST_SESSION_MISMATCH"=>"Only the launching session can perform this action.",
   _=>"Playtest request was rejected. Check session, ownership, pinned package and idempotency input."};
  return AuthResult<JsonElement>.Fail(code,message,value.GetProperty("status").GetInt32(),errors);
 }
}
