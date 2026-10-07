using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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
  var config=options.Value;
  if(!config.Enabled)return AuthResult<PlaytestLaunch>.Fail("PLAYTEST_LAUNCH_UNAVAILABLE","Playtest launch signing is not configured.",503);
  // Validate signing material before any Trial mutation; never store raw JWT in the receipt.
  var credentials=new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config.SigningKey)),SecurityAlgorithms.HmacSha256);
  if(credentials.Key.KeySize<256)throw new InvalidOperationException("Invalid playtest signing key.");
  var result=await Execute("Start",actor,family,playtest,null,request,key,ct);
  if(!result.IsSuccess)return new(default,result.Error);
  var launch=result.Value.Deserialize<PlaytestLaunch>(Json)!;
  var claims=new[]{new Claim("purpose","playtest"),new Claim(JwtRegisteredClaimNames.Sub,launch.ActorId.ToString()),new Claim("sid",launch.FamilyId.ToString()),new Claim("playtest_id",launch.PlaytestId.ToString()),new Claim("scenario_version_id",launch.ScenarioVersionId.ToString()),new Claim("artifact_id",launch.ArtifactId.ToString()),new Claim("validation_run_id",launch.ValidationRunId.ToString()),new Claim("package_hash",launch.PackageHash),new Claim("manifest_hash",launch.ManifestHash),new Claim("build_target",launch.BuildTarget),new Claim("runtime_version",launch.RuntimeVersion),new Claim("protocol_version",launch.ProtocolVersion),new Claim("manifest_schema_version",launch.ManifestSchemaVersion),new Claim(JwtRegisteredClaimNames.Jti,launch.PlaytestId.ToString())};
  var jwt=new JwtSecurityToken(config.Issuer,config.Audience,claims,launch.IssuedAt,launch.ExpiresAt,credentials);
  return AuthResult<PlaytestLaunch>.Ok(launch with{LaunchGrant=new JwtSecurityTokenHandler().WriteToken(jwt)});
 }
 private async Task<AuthResult<JsonElement>> Execute(string action,Guid actor,Guid family,Guid resource,Guid? building,object input,string? key,CancellationToken ct)
 {
  await db.Database.OpenConnectionAsync(ct);
  await using var command=new NpgsqlCommand("SELECT playtest_lifecycle_gate(@action,@actor,@family,@resource,@building,@input,@key)::text",(NpgsqlConnection)db.Database.GetDbConnection());
  command.Parameters.AddWithValue("action",action);command.Parameters.AddWithValue("actor",actor);command.Parameters.AddWithValue("family",family);command.Parameters.AddWithValue("resource",resource);
  command.Parameters.AddWithValue("building",NpgsqlDbType.Uuid,(object?)building??DBNull.Value);command.Parameters.AddWithValue("input",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(input,Json));command.Parameters.AddWithValue("key",NpgsqlDbType.Text,(object?)key??DBNull.Value);
  using var document=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);var value=document.RootElement;
  if(value.GetProperty("code").GetString()=="OK")return AuthResult<JsonElement>.Ok(value.Clone());
  var errors=value.TryGetProperty("errors",out var fields)?fields.Deserialize<Dictionary<string,string[]>>():null;
  var code=value.GetProperty("code").GetString()!;
  var message=code switch{"RUNTIME_INCOMPATIBLE"=>"Runtime is not active or does not support this package's version, protocol, schema and capabilities.","PLAYTEST_ENTITLEMENT_REQUIRED"=>"This Building needs a current paid service entitlement or remaining Trial playtest units.","DRAFT_SNAPSHOT_REQUIRED"=>"Snapshot the current draft before requesting its package build.","PLAYTEST_PACKAGE_REQUIRED"=>"Build and validate a PlaytestPackage for this exact immutable version first.","PLAYTEST_GRANT_EXPIRED"=>"The original five-minute launch grant has expired; it cannot be renewed by replay.",_=>"Playtest request was rejected. Check session, ownership, pinned package and idempotency input."};
  return AuthResult<JsonElement>.Fail(code,message,value.GetProperty("status").GetInt32(),errors);
 }
}
