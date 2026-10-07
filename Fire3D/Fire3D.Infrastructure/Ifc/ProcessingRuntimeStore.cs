using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
namespace Fire3D.Infrastructure.Ifc;
public sealed class ProcessingRuntimeStore(Fire3DDbContext db, IConfiguration configuration) : IRevisionProcessingStore,IProcessingWorkerGate,IProcessingDispatchGate
{
    public async Task<AuthResult<Guid>> RequestAsync(Guid actor,Guid revision,string key,CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("SELECT request_revision_processing(@actor,@revision,@key)::text",(NpgsqlConnection)db.Database.GetDbConnection());
        cmd.Parameters.AddWithValue("actor",actor);cmd.Parameters.AddWithValue("revision",revision);cmd.Parameters.AddWithValue("key",key);
        var value=Parse(await cmd.ExecuteScalarAsync(ct));
        return Error(value) is { } error ? new(default,error) : AuthResult<Guid>.Ok(value.GetProperty("jobId").GetGuid());
    }
    public async Task<AuthResult<JsonElement>> ExecuteAsync(string action,Guid job,JsonElement input,CancellationToken ct)
    {
        // Separate restricted executor: API identity cannot execute the worker gate or directly write provenance.
        await using var connection=new NpgsqlConnection(configuration.GetConnectionString("ProcessingExecutor"));await connection.OpenAsync(ct);
        await using var cmd=new NpgsqlCommand("SELECT processing_worker_gate(@action,@job,@input)::text",connection);
        cmd.Parameters.AddWithValue("action",action);cmd.Parameters.AddWithValue("job",job);cmd.Parameters.AddWithValue("input",NpgsqlDbType.Jsonb,input.GetRawText());
        var value=Parse(await cmd.ExecuteScalarAsync(ct));
        if(Error(value) is { } error) return new(default,error);
        if(action=="Claim")
        {
            await using var context=new NpgsqlCommand("SELECT processing_worker_context(@attempt,@token)::text",connection);
            context.Parameters.AddWithValue("attempt",value.GetProperty("attemptId").GetGuid());context.Parameters.AddWithValue("token",value.GetProperty("leaseToken").GetGuid());
            if(await context.ExecuteScalarAsync(ct) is string snapshot)
            {
                var node=System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText())!;
                foreach(var field in System.Text.Json.Nodes.JsonNode.Parse(snapshot)!.AsObject())node[field.Key]=field.Value?.DeepClone();
                value=JsonSerializer.SerializeToElement(node);
            }
        }
        return AuthResult<JsonElement>.Ok(value);
    }
    public async Task<JsonElement> ExecuteAsync(string action,string? key,Guid? token,Guid? receipt,CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var cmd=new NpgsqlCommand("SELECT processing_dispatch_gate(@action,@key,@token,@receipt)::text",(NpgsqlConnection)db.Database.GetDbConnection());
        cmd.Parameters.AddWithValue("action",action);cmd.Parameters.AddWithValue("key",NpgsqlDbType.Text,(object?)key??DBNull.Value);
        cmd.Parameters.AddWithValue("token",NpgsqlDbType.Uuid,(object?)token??DBNull.Value);cmd.Parameters.AddWithValue("receipt",NpgsqlDbType.Uuid,(object?)receipt??DBNull.Value);
        return Parse(await cmd.ExecuteScalarAsync(ct));
    }
    private static JsonElement Parse(object? value) { using var json=JsonDocument.Parse((string)value!);return json.RootElement.Clone(); }
    private static AuthError? Error(JsonElement root)=>root.GetProperty("code").GetString() is "OK" ? null : new(root.GetProperty("code").GetString()!,"Processing operation was rejected. Check scope, input and current lease.",root.GetProperty("status").GetInt32());
}
