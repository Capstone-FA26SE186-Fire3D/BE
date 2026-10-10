using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios;
using Fire3D.Application.Scenarios.Commands.CreateScenario;
using Fire3D.Application.Scenarios.Commands.CreateScenarioDraft;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;
using Fire3D.Application.Scenarios.Dto;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
namespace Fire3D.Infrastructure.Scenarios;
public sealed class ScenarioWriteStore(Fire3DDbContext db) : IScenarioWriteStore, IScenarioPackageBuildStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Task<AuthResult<Guid>> CreateScenarioAsync(Guid actorId, Guid buildingId, Guid? organizationId, CreateScenarioRequest request, CancellationToken ct, string? key=null)
        => IdAsync("CreateScenario",actorId,buildingId,new{name=request.Name},key,null,ct);
    public Task<AuthResult<Guid>> CreateScenarioDraftAsync(Guid actorId, Guid scenarioId, Guid? organizationId, CreateScenarioDraftRequest request, CancellationToken ct, string? key=null)
        => IdAsync("CreateDraft",actorId,scenarioId,request,key,null,ct);
    /// <summary>Legacy typed overload; serializes exactly as the historical PUT body binding did.</summary>
    public Task<AuthResult<uint>> UpdateScenarioDraftAsync(Guid actorId, Guid draftId, uint expectedVersion, ScenarioDraftStateDto state, Guid? organizationId, CancellationToken ct)
        => UpdateScenarioDraftAsync(actorId, draftId, expectedVersion, JsonSerializer.SerializeToNode(state, Json)!, organizationId, ct);
    public async Task<AuthResult<uint>> UpdateScenarioDraftAsync(Guid actorId, Guid draftId, uint expectedVersion, JsonNode state, Guid? organizationId, CancellationToken ct)
    {
        var result=await GateAsync("UpdateDraft",actorId,draftId,state,null,expectedVersion,ct);
        return result.IsSuccess ? AuthResult<uint>.Ok(result.Value.GetProperty("revision").GetUInt32()) : new(default,result.Error);
    }
    public async Task<AuthResult<Guid>> SnapshotScenarioDraftAsync(Guid actorId,Guid draftId,Guid? organizationId,CancellationToken ct,string? key=null,uint? expectedVersion=null)
    {
        // Read only the authorized state. Gate rechecks xmin under lock before adopting it.
        var draft=await db.ScenarioDrafts.AsNoTracking().Where(x=>x.Id==draftId && (!organizationId.HasValue || x.OrganizationId==organizationId)).SingleOrDefaultAsync(ct);
        if(draft is null) return AuthResult<Guid>.Fail("NOT_FOUND","Draft not found in scope.",404);
        // Versioned drafts were validated by the handler; the legacy structural validator applies to legacy drafts only.
        if (draft.Version==expectedVersion && Fire3D.Application.Editor.EditorContract.DeclaredVersion(draft.State) is null)
        {
            var issues=ScenarioDraftStructuralValidator.Validate(draft.State);
            if(issues.Count>0) return AuthResult<Guid>.Fail("VALIDATION_ERROR","Draft structure is not valid.",400,issues.GroupBy(x=>x.Path).ToDictionary(x=>x.Key,x=>x.Select(y=>y.Message).ToArray()));
        }
        return await IdAsync("Snapshot",actorId,draftId,new{},key,expectedVersion,ct);
    }
    public Task<AuthResult<Guid>> BuildAsync(Guid actor,Guid version,PackageBuildRequest request,string? key,CancellationToken ct)
        => IdAsync("BuildPackage",actor,version,request,key,null,ct);
    private async Task<AuthResult<Guid>> IdAsync(string action,Guid actor,Guid resource,object input,string? key,uint? revision,CancellationToken ct)
    {
        var value=await GateAsync(action,actor,resource,input,key,revision,ct);
        return value.IsSuccess?AuthResult<Guid>.Ok(value.Value.GetProperty("id").GetGuid()):new(default,value.Error);
    }
    private async Task<AuthResult<JsonElement>> GateAsync(string action,Guid actor,Guid resource,object input,string? key,uint? revision,CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command=new NpgsqlCommand("SELECT scenario_authoring_gate(@action,@actor,@resource,@input,@key,@revision)::text",(NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("action",action);command.Parameters.AddWithValue("actor",actor);command.Parameters.AddWithValue("resource",resource);
        command.Parameters.AddWithValue("input",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(input,Json));
        command.Parameters.AddWithValue("key",NpgsqlDbType.Text,(object?)key??DBNull.Value);command.Parameters.AddWithValue("revision",NpgsqlDbType.Bigint,revision.HasValue?(object)(long)revision.Value:DBNull.Value);
        using var response=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);var value=response.RootElement;
        if(value.GetProperty("code").GetString()=="OK")return AuthResult<JsonElement>.Ok(value.Clone());
        var errors=value.TryGetProperty("errors",out var fields)?fields.Deserialize<Dictionary<string,string[]>>():null;
        return AuthResult<JsonElement>.Fail(value.GetProperty("code").GetString()!,"Authoring request was rejected. Check scope, fields and current revision.",value.GetProperty("status").GetInt32(),errors);
    }
}
