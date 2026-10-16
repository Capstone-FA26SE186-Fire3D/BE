using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Fire3D.Infrastructure.Ifc;

public sealed class RevisionStyles(Fire3DDbContext db) : IRevisionStyles
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<AuthResult<RevisionStyleView>> GetAsync(Guid actor, Guid family, Guid revision, CancellationToken ct) =>
        Gate("Get", actor, family, revision, new { }, null, ct);

    public Task<AuthResult<RevisionStyleView>> SaveAsync(Guid actor, Guid family, Guid revision, long expected, RevisionStyleRequest request, CancellationToken ct) =>
        Gate("Save", actor, family, revision, request, expected, ct);

    private async Task<AuthResult<RevisionStyleView>> Gate(string action, Guid actor, Guid family, Guid revision, object input, long? expected, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT revision_style_gate(@action,@actor,@family,@revision,@input,@expected)::text", (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("action", action); command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("family", family); command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("input", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(input, Json));
        command.Parameters.AddWithValue("expected", NpgsqlDbType.Bigint, (object?)expected ?? DBNull.Value);
        using var document = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);
        var value = document.RootElement;
        var code = value.GetProperty("code").GetString()!;
        if (code == "OK") return AuthResult<RevisionStyleView>.Ok(value.GetProperty("result").Deserialize<RevisionStyleView>(Json)!);
        var errors = value.TryGetProperty("issues", out var issues)
            ? issues.EnumerateArray().GroupBy(i => i.GetProperty("path").GetString()!.TrimStart('$', '.'))
                .ToDictionary(g => g.Key, g => g.Select(i => $"{i.GetProperty("code").GetString()}: {i.GetProperty("message").GetString()}").ToArray())
            : null;
        return AuthResult<RevisionStyleView>.Fail(code, code switch
        {
            "STYLE_SELECTION_INVALID" => "The preset or colour scheme is not a published version of an active Library item.",
            "REVISION_NOT_EDITABLE" => "A superseded or rejected revision keeps its style.",
            "PRECONDITION_REQUIRED" => "Send the ETag from GET style in If-Match.",
            "PRECONDITION_FAILED" => "The style changed since you read it. Reload and retry.",
            "NOT_FOUND" => "Revision not found.",
            _ => "Style request was rejected."
        }, value.GetProperty("status").GetInt32(), errors);
    }
}
