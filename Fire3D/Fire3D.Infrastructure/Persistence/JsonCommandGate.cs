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
  if(function is not ("release_access_gate" or "publish_release_gate" or "support_command_gate"))throw new ArgumentException("Unknown gate.",nameof(function));
  await db.Database.OpenConnectionAsync(ct);
  await using var command=new NpgsqlCommand($"SELECT {function}(@action,@actor,@family,@resource,@input,@key,@expected)::text",(NpgsqlConnection)db.Database.GetDbConnection());
  command.Parameters.AddWithValue("action",action);command.Parameters.AddWithValue("actor",actor);
  command.Parameters.AddWithValue("family",NpgsqlDbType.Uuid,(object?)family??DBNull.Value);command.Parameters.AddWithValue("resource",NpgsqlDbType.Uuid,(object?)resource??DBNull.Value);
  command.Parameters.AddWithValue("input",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(input,Json));command.Parameters.AddWithValue("key",NpgsqlDbType.Text,(object?)key??DBNull.Value);command.Parameters.AddWithValue("expected",NpgsqlDbType.Bigint,(object?)expected??DBNull.Value);
  using var doc=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);var value=doc.RootElement;
  if(value.GetProperty("code").GetString()=="OK")return AuthResult<JsonElement>.Ok(value.GetProperty("result").Clone());
  var errors=value.TryGetProperty("errors",out var fields)?fields.Deserialize<Dictionary<string,string[]>>():null;
  return AuthResult<JsonElement>.Fail(value.GetProperty("code").GetString()!,"The operation was rejected. Check lifecycle, ownership, revision and required provenance.",value.GetProperty("status").GetInt32(),errors);
 }
}
