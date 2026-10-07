using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
namespace Fire3D.Infrastructure.Scenarios;
public sealed class ScenarioReadinessStore(Fire3DDbContext db):IScenarioReadinessStore
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public async Task<AuthResult<JsonElement>> ExecuteAsync(string action,Guid actor,Guid version,Guid? revision,object input,string? key,CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command=new NpgsqlCommand("SELECT scenario_readiness_gate(@action,@actor,@version,@revision,@input,@key)::text",(NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("action",action);command.Parameters.AddWithValue("actor",actor);command.Parameters.AddWithValue("version",version);
        command.Parameters.AddWithValue("revision",NpgsqlDbType.Uuid,(object?)revision??DBNull.Value);command.Parameters.AddWithValue("input",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(input,Json));command.Parameters.AddWithValue("key",NpgsqlDbType.Text,(object?)key??DBNull.Value);
        using var response=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);var value=response.RootElement;
        if(value.GetProperty("code").GetString()=="OK")return AuthResult<JsonElement>.Ok(value.Clone());
        var errors=value.TryGetProperty("errors",out var fields)?fields.Deserialize<Dictionary<string,string[]>>():null;
        return AuthResult<JsonElement>.Fail(value.GetProperty("code").GetString()!,"Review request was rejected. Check ownership, exact validation provenance and content hashes.",value.GetProperty("status").GetInt32(),errors);
    }
}
