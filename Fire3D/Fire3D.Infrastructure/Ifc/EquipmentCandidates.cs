using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Fire3D.Infrastructure.Ifc;

public sealed class EquipmentCandidates(Fire3DDbContext db) : IEquipmentCandidates
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<AuthResult<EquipmentCandidatesView>> GetAsync(Guid actor, Guid family, Guid revision, CancellationToken ct) =>
        Gate("Get", actor, family, revision, new { }, null, ct);

    public Task<AuthResult<EquipmentCandidatesView>> SaveAsync(Guid actor, Guid family, Guid revision, long expected, EquipmentDecisionsRequest request, CancellationToken ct) =>
        Gate("Save", actor, family, revision, request, expected, ct);

    private async Task<AuthResult<EquipmentCandidatesView>> Gate(string action, Guid actor, Guid family, Guid revision, object input, long? expected, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT revision_equipment_gate(@action,@actor,@family,@revision,@input,@expected)::text", (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("action", action); command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("family", family); command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("input", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(input, Json));
        command.Parameters.AddWithValue("expected", NpgsqlDbType.Bigint, (object?)expected ?? DBNull.Value);
        using var document = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);
        var value = document.RootElement;
        var code = value.GetProperty("code").GetString()!;
        if (code == "OK") return AuthResult<EquipmentCandidatesView>.Ok(value.GetProperty("result").Deserialize<EquipmentCandidatesView>(Json)!);
        var errors = value.TryGetProperty("issues", out var issues)
            ? issues.EnumerateArray().GroupBy(i => i.GetProperty("path").GetString()!.TrimStart('$', '.'))
                .ToDictionary(g => g.Key, g => g.Select(i => $"{i.GetProperty("code").GetString()}: {i.GetProperty("message").GetString()}").ToArray())
            : null;
        return AuthResult<EquipmentCandidatesView>.Fail(code, code switch
        {
            "EQUIPMENT_CANDIDATES_UNAVAILABLE" => "The revision has no accepted geometry with detected equipment yet.",
            "EQUIPMENT_DECISION_INVALID" => "Some decisions name an unknown candidate or an unsupported catalogKey.",
            "PRECONDITION_REQUIRED" => "Send the ETag from GET equipment-candidates in If-Match.",
            "PRECONDITION_FAILED" => "Decisions changed since you read them. Reload and retry.",
            "NOT_FOUND" => "Revision not found.",
            _ => "Equipment request was rejected."
        }, value.GetProperty("status").GetInt32(), errors);
    }
}
