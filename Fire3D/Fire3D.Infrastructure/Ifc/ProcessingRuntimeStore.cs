using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
namespace Fire3D.Infrastructure.Ifc;
/// <summary>
/// BE#118: the ProcessingExecutor login cannot run the worker gates (not a member of fet3d_processing_executor) or cannot log in.
/// A deployment configuration problem, answered 503 with Code instead of a generic 500.
/// </summary>
public sealed class ProcessingExecutorUnavailableException(string code,Exception inner):Exception("The processing executor login is not usable: "+code,inner)
{
    public string Code { get; }=code;
    /// <summary>Configuration code for a database exception, or null when it is not a login or permission problem.</summary>
    public static string? CodeFor(Exception ex)=>(ex as PostgresException??ex.InnerException as PostgresException) switch
    {
        // Only the login lacking EXECUTE on a worker gate (or the schema): a 42501 raised inside a gate body (RLS, an owner grant)
        // is a defect, not a deployment setting, and stays a 500.
        { SqlState: PostgresErrorCodes.InsufficientPrivilege } p when p.MessageText.StartsWith("permission denied for function",StringComparison.Ordinal)
            || p.MessageText.StartsWith("permission denied for schema",StringComparison.Ordinal)=>"PROCESSING_EXECUTOR_FORBIDDEN",
        { SqlState: PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification }=>"PROCESSING_EXECUTOR_LOGIN_FAILED",
        _=>null
    };
}
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
    // Separate restricted executor: API identity cannot execute the worker gate or directly write provenance.
    public Task<AuthResult<JsonElement>> ExecuteAsync(string action,Guid job,JsonElement input,CancellationToken ct)=>ExecutorAsync(async connection=>
    {
        await using var cmd=new NpgsqlCommand("SELECT processing_worker_gate(@action,@job,@input)::text",connection);
        cmd.Parameters.AddWithValue("action",action);cmd.Parameters.AddWithValue("job",job);cmd.Parameters.AddWithValue("input",NpgsqlDbType.Jsonb,input.GetRawText());
        var value=Parse(await cmd.ExecuteScalarAsync(ct));
        if(Error(value) is { } error) return new AuthResult<JsonElement>(default,error);
        if(action=="Claim")value=await WithContextAsync(connection,value,ct);
        return AuthResult<JsonElement>.Ok(value);
    },ct);
    public Task<AuthResult<JsonElement?>> LeasePackageAsync(JsonElement input,CancellationToken ct)=>ExecutorAsync(async connection=>
    {
        await using var cmd=new NpgsqlCommand("SELECT processing_package_lease_gate(@input)::text",connection);
        cmd.Parameters.AddWithValue("input",NpgsqlDbType.Jsonb,input.GetRawText());
        var value=Parse(await cmd.ExecuteScalarAsync(ct));
        if(value.GetProperty("code").GetString()=="EMPTY")return AuthResult<JsonElement?>.Ok(null);
        if(Error(value) is { } error) return new AuthResult<JsonElement?>(null,error);
        return AuthResult<JsonElement?>.Ok(await WithContextAsync(connection,value,ct));
    },ct);
    public Task<JsonElement?> ContextAsync(Guid attempt,Guid leaseToken,CancellationToken ct)=>ExecutorAsync(connection=>ContextAsync(connection,attempt,leaseToken,ct),ct);
    // BE#118: a login that is not a member of fet3d_processing_executor (or a wrong password) is a deployment problem, not a 500.
    private async Task<T> ExecutorAsync<T>(Func<NpgsqlConnection,Task<T>> work,CancellationToken ct)
    {
        try
        {
            await using var connection=new NpgsqlConnection(configuration.GetConnectionString("ProcessingExecutor"));await connection.OpenAsync(ct);
            return await work(connection);
        }
        catch(Exception ex) when(ProcessingExecutorUnavailableException.CodeFor(ex) is { } code) { throw new ProcessingExecutorUnavailableException(code,ex); }
    }
    private static async Task<JsonElement?> ContextAsync(NpgsqlConnection connection,Guid attempt,Guid token,CancellationToken ct)
    {
        await using var context=new NpgsqlCommand("SELECT processing_worker_context(@attempt,@token)::text",connection);
        context.Parameters.AddWithValue("attempt",attempt);context.Parameters.AddWithValue("token",token);
        return await context.ExecuteScalarAsync(ct) is string snapshot ? Parse(snapshot) : null;
    }
    private static async Task<JsonElement> WithContextAsync(NpgsqlConnection connection,JsonElement value,CancellationToken ct)
    {
        if(await ContextAsync(connection,value.GetProperty("attemptId").GetGuid(),value.GetProperty("leaseToken").GetGuid(),ct) is not { } snapshot)return value;
        var node=System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText())!;
        foreach(var field in System.Text.Json.Nodes.JsonNode.Parse(snapshot.GetRawText())!.AsObject())node[field.Key]=field.Value?.DeepClone();
        return JsonSerializer.SerializeToElement(node);
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
