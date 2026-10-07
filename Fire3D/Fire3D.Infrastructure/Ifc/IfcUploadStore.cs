using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Application.Ifc.Commands.InitiateUpload;
using Fire3D.Application.Ifc.Commands.FinalizeUpload;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Fire3D.Infrastructure.Ifc;

public sealed class IfcUploadStore(Fire3DDbContext db) : IIfcUploadStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Task<AuthResult<IfcUploadIntent>> InitiateAsync(Guid actor, Guid building, InitiateIfcUploadRequest input, string key, CancellationToken ct) =>
        Gate<IfcUploadIntent>("Initiate", actor, building, new { input.FileSizeBytes, input.OriginalFilename, input.VersionLabel, input.Sha256Hash, BuildingId = building }, key, null, null, "intent", ct);
    public Task<AuthResult<IfcUploadIntent>> ReadAsync(Guid actor, Guid revision, FinalizeIfcUploadRequest input, CancellationToken ct) =>
        Gate<IfcUploadIntent>("Read", actor, revision, input, null, null, null, "intent", ct);
    public Task<AuthResult<IfcUploadAttempt>> ClaimAsync(Guid actor, Guid revision, FinalizeIfcUploadRequest input, string etag, CancellationToken ct) =>
        Gate<IfcUploadAttempt>("Claim", actor, revision, input, null, null, etag, "attempt", ct);
    public Task<AuthResult<bool>> AdoptAsync(Guid actor, Guid revision, Guid attempt, FinalizeIfcUploadRequest input, string etag, CancellationToken ct) =>
        Gate<bool>("Adopt", actor, revision, input, null, attempt, etag, null, ct);
    private async Task<AuthResult<T>> Gate<T>(string action, Guid actor, Guid resource, object input, string? key, Guid? attempt, string? etag, string? field, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT ifc_upload_gate(@action,@actor,@resource,@input,@key,@attempt,@etag)::text", (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        cmd.Parameters.AddWithValue("action", action); cmd.Parameters.AddWithValue("actor", actor); cmd.Parameters.AddWithValue("resource", resource);
        cmd.Parameters.AddWithValue("input", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(input, Json));
        cmd.Parameters.AddWithValue("key", NpgsqlDbType.Text, (object?)key ?? DBNull.Value);
        cmd.Parameters.AddWithValue("attempt", NpgsqlDbType.Uuid, (object?)attempt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("etag", NpgsqlDbType.Text, (object?)etag ?? DBNull.Value);
        using var result = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync(ct))!);
        var root = result.RootElement; var code = root.GetProperty("code").GetString()!;
        if (code != "OK") return AuthResult<T>.Fail(code, "IFC upload could not be completed. Check the intent and retry with the same input.", root.GetProperty("status").GetInt32(),
            retryAfterSeconds: root.TryGetProperty("retryAfter", out var retry) ? retry.GetInt32() : null);
        return AuthResult<T>.Ok(field is null ? (T)(object)true : root.GetProperty(field).Deserialize<T>(Json)!);
    }
}
