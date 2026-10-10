using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Content;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Fire3D.Infrastructure.Content;

public sealed class ContentGates(Fire3DDbContext db) : IContentGates
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<AuthResult<JsonElement>> LearnPublicAsync(string action, Guid? actor, Guid? family, object input, CancellationToken ct) =>
        Run("SELECT learn_public_gate(@action,@actor,@family,@input)::text", action, actor, family, null, input, null, null, ct);
    public Task<AuthResult<JsonElement>> LearnAdminAsync(string action, Guid actor, Guid family, Guid resource, object input, string? key, long? expected, CancellationToken ct) =>
        Run("SELECT learn_admin_gate(@action,@actor,@family,@resource,@input,@key,@expected)::text", action, actor, family, resource, input, key, expected, ct);
    public Task<AuthResult<JsonElement>> LibraryAsync(string action, Guid actor, Guid family, Guid resource, object input, string? key, long? expected, CancellationToken ct) =>
        Run("SELECT library_gate(@action,@actor,@family,@resource,@input,@key,@expected)::text", action, actor, family, resource, input, key, expected, ct);

    private async Task<AuthResult<JsonElement>> Run(string sql, string action, Guid? actor, Guid? family, Guid? resource, object input, string? key, long? expected, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Uuid, (object?)actor ?? DBNull.Value);
        command.Parameters.AddWithValue("family", NpgsqlDbType.Uuid, (object?)family ?? DBNull.Value);
        if (sql.Contains("@resource")) command.Parameters.AddWithValue("resource", NpgsqlDbType.Uuid, (object?)resource ?? Guid.Empty);
        command.Parameters.AddWithValue("input", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(input, Json));
        if (sql.Contains("@key")) command.Parameters.AddWithValue("key", NpgsqlDbType.Text, (object?)key ?? DBNull.Value);
        if (sql.Contains("@expected")) command.Parameters.AddWithValue("expected", NpgsqlDbType.Bigint, (object?)expected ?? DBNull.Value);
        using var document = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);
        var value = document.RootElement;
        if (value.GetProperty("code").GetString() == "OK") return AuthResult<JsonElement>.Ok(value.GetProperty("result").Clone());
        var code = value.GetProperty("code").GetString()!;
        var message = code switch
        {
            "LEARN_POST_UNAVAILABLE" => "This post is not public.",
            "LEARN_SOURCE_NOT_APPROVED" => "Every cited source must be an approved Common source before publishing.",
            "LEARN_SOURCE_INVALID" => "Cite only registered Common knowledge sources.",
            "LEARN_SITUATION_INVALID" => "Use active Learn situations.",
            "LEARN_STATE_CONFLICT" => "The post is not in a state that allows this action.",
            "LEARN_VERSION_PUBLISHED" or "LIBRARY_VERSION_PUBLISHED" => "Published versions are immutable; create a new version.",
            "LEARN_POST_DELETED" => "Restore the post first.",
            "SLUG_EXISTS" => "The slug is already used.",
            "LIBRARY_CODE_EXISTS" => "The library code is already used.",
            _ => "Content request was rejected."
        };
        return AuthResult<JsonElement>.Fail(code, message, value.GetProperty("status").GetInt32());
    }
}
