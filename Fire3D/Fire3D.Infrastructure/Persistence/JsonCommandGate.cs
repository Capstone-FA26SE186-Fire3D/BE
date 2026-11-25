using System.Text.Json;
using Fire3D.Application.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
namespace Fire3D.Infrastructure.Persistence;
internal static class JsonCommandGate
{
 public static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
 public static async Task<AuthResult<JsonElement>> Execute(Fire3DDbContext db,string function,string action,Guid actor,Guid? family,Guid? resource,object input,string? key,long? expected,CancellationToken ct)
 {
  if(function is not ("release_access_gate" or "publish_release_gate" or "support_command_gate" or "scenario_review_read_gate" or "authoring_readiness_gate" or "training_list_gate" or "expert_review_gate" or "fire_infrastructure_gate" or "plan_dossier_gate" or "ifc_processing_policy_gate"))throw new ArgumentException("Unknown gate.",nameof(function));
  await db.Database.OpenConnectionAsync(ct);
  await using var command=new NpgsqlCommand($"SELECT {function}(@action,@actor,@family,@resource,@input,@key,@expected)::text",(NpgsqlConnection)db.Database.GetDbConnection());
  command.Parameters.AddWithValue("action",action);command.Parameters.AddWithValue("actor",actor);
  command.Parameters.AddWithValue("family",NpgsqlDbType.Uuid,(object?)family??DBNull.Value);command.Parameters.AddWithValue("resource",NpgsqlDbType.Uuid,(object?)resource??DBNull.Value);
  command.Parameters.AddWithValue("input",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(input,Json));command.Parameters.AddWithValue("key",NpgsqlDbType.Text,(object?)key??DBNull.Value);command.Parameters.AddWithValue("expected",NpgsqlDbType.Bigint,(object?)expected??DBNull.Value);
  using var doc=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);var value=doc.RootElement;
  if(value.GetProperty("code").GetString()=="OK")return AuthResult<JsonElement>.Ok(value.GetProperty("result").Clone());
  var errors=value.TryGetProperty("errors",out var fields)?fields.Deserialize<Dictionary<string,string[]>>():null;
  var code=value.GetProperty("code").GetString()!;
  return AuthResult<JsonElement>.Fail(code,Message(code),value.GetProperty("status").GetInt32(),errors);
 }
 // Codes a client shows to a person get their own sentence; everything else keeps the generic one.
 private static string Message(string code)=>code switch
 {
  "PLAN_SCENARIO_COVERAGE_INCOMPLETE"=>"Publishing needs an Approved situation in working hours and one outside them on this fire infrastructure (errors.workingHours lists what is missing).",
  "FIRE_INFRASTRUCTURE_NOT_APPROVED"=>"Use a fire infrastructure version that passed thẩm định (Approved).",
  "FIRE_INFRASTRUCTURE_LOCKED"=>"An Approved fire infrastructure is locked; create a new version to change it.",
  "FIRE_INFRASTRUCTURE_NOT_DRAFT"=>"Only a Draft fire infrastructure version can change; create a new version after a decision.",
  "FIRE_INFRASTRUCTURE_OPEN_VERSION_EXISTS"=>"This revision already has a Draft or Submitted fire infrastructure version (errors.id).",
  "FIRE_INFRASTRUCTURE_INVALID"=>"The fire infrastructure needs geometry, runtimeVersion and items before it can be submitted.",
  "FIRE_INFRASTRUCTURE_NOT_PENDING"=>"This fire infrastructure version was already decided.",
  "CONTENT_REVIEW_NOT_PENDING"=>"This situation was already decided.",
  "CONTENT_HASH_MISMATCH"=>"The content changed after it was submitted; it cannot be decided.",
  "PRECONDITION_FAILED"=>"The data changed since you read it (someone else decided or saved first). Reload and try again.",
  "PRECONDITION_REQUIRED"=>"Send the current ETag in If-Match.",
  "IDEMPOTENCY_KEY_REQUIRED"=>"Send an Idempotency-Key header.",
  "IDEMPOTENCY_KEY_CONFLICT"=>"This Idempotency-Key was already used with a different request.",
  "NOT_FOUND"=>"Not found, or not visible to this account.",
  "FORBIDDEN"=>"This account's role cannot do this.",
  "UNAUTHORIZED"=>"The session is no longer active.",
  _=>"The operation was rejected. Check lifecycle, ownership, revision and required provenance."
 };
}
