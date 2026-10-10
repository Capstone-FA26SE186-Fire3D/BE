using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fire3D.Application.Authentication;
using Fire3D.Application.Learning;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using NpgsqlTypes;

namespace Fire3D.Infrastructure.Learning;

public sealed partial class LearnerSessions(Fire3DDbContext db, IOptions<LearnerSessionOptions> options) : ILearnerSessions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Modes = ["Learn", "Guided", "Assessment"];
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")] private static partial Regex Semver();

    public async Task<AuthResult<TrainingSessionView>> PrepareAsync(LearnerCaller caller, PrepareTrainingSessionRequest request, string? key, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.TrainingId == Guid.Empty) errors["trainingId"] = ["A Training ID is required."];
        if (!Modes.Contains(request.Mode)) errors["mode"] = ["Use Learn, Guided or Assessment."];
        if (request.RuntimeVersion is null || request.RuntimeVersion.Length > 50 || !Semver().IsMatch(request.RuntimeVersion)) errors["runtimeVersion"] = ["Use major.minor.patch from the active runtime catalog."];
        if (errors.Count > 0) return AuthResult<TrainingSessionView>.Fail("VALIDATION_ERROR", "Session preparation validation failed.", 400, errors);
        var result = await Gate("Prepare", caller, Guid.Empty, request, key, ct);
        return result.IsSuccess ? AuthResult<TrainingSessionView>.Ok(result.Value.GetProperty("result").GetProperty("session").Deserialize<TrainingSessionView>(Json)!) : new(default, result.Error);
    }

    public async Task<AuthResult<TrainingSessionView>> GetAsync(LearnerCaller caller, Guid id, CancellationToken ct) => View(await Gate("Get", caller, id, new { }, null, ct));

    public async Task<AuthResult<StartTrainingSessionResponse>> StartAsync(LearnerCaller caller, Guid id, string? key, CancellationToken ct)
    {
        // Signing material is validated before the gate so a disabled deployment never holds a seat.
        if (Signers() is not { } signers) return AuthResult<StartTrainingSessionResponse>.Fail("LEARNER_LAUNCH_UNAVAILABLE", "Learner launch signing is not configured.", 503);
        var result = await Gate("Start", caller, id, new { continuationTtlSeconds = Ttl() }, key, ct);
        if (!result.IsSuccess) return new(default, result.Error);
        var body = result.Value.GetProperty("result");
        return AuthResult<StartTrainingSessionResponse>.Ok(new(body.GetProperty("session").Deserialize<TrainingSessionView>(Json)!,
            Launch(body.GetProperty("launch"), signers.Launch), Continuation(caller.ActorId, body.GetProperty("continuation"), id, signers.Continuation)));
    }

    public async Task<AuthResult<TrainingSessionView>> LaunchedAsync(LearnerCaller caller, Guid id, LaunchedTrainingSessionRequest request, CancellationToken ct) =>
        request.Generation < 1 ? AuthResult<TrainingSessionView>.Fail("VALIDATION_ERROR", "generation identifies the launch grant.", 422) : View(await Gate("Launched", caller, id, request, null, ct));

    public async Task<AuthResult<HeartbeatResponse>> HeartbeatAsync(LearnerCaller caller, Guid id, CancellationToken ct)
    {
        var result = await Gate("Heartbeat", caller, id, new { }, null, ct);
        return result.IsSuccess ? AuthResult<HeartbeatResponse>.Ok(result.Value.GetProperty("result").Deserialize<HeartbeatResponse>(Json)!) : new(default, result.Error);
    }

    public async Task<AuthResult<TrainingEventsResponse>> RecordEventsAsync(LearnerCaller caller, Guid id, TrainingEventsRequest request, CancellationToken ct)
    {
        if (ValidateEvents(request) is { } invalid) return AuthResult<TrainingEventsResponse>.Fail("VALIDATION_ERROR", invalid, 422);
        var result = await Gate("Events", caller, id, new
        {
            events = request.Events.Select(e => new { eventId = e.EventId, sequence = e.Sequence, schemaVersion = e.SchemaVersion, type = e.Type,
                occurredAt = e.OccurredAt.ToUniversalTime().ToString("O"), elapsedMs = e.ElapsedMs, payload = e.Payload })
        }, null, ct);
        return result.IsSuccess ? AuthResult<TrainingEventsResponse>.Ok(result.Value.GetProperty("result").Deserialize<TrainingEventsResponse>(Json)!) : new(default, result.Error);
    }

    public async Task<AuthResult<(int Status, CompleteTrainingSessionResponse Body)>> CompleteAsync(LearnerCaller caller, Guid id, CompleteTrainingSessionRequest request, string? key, CancellationToken ct)
    {
        if (request.LastEventSequence < 0 || request.EndReason is not ("Finished" or "Abandoned" or "TimedOut"))
            return AuthResult<(int, CompleteTrainingSessionResponse)>.Fail("VALIDATION_ERROR", "lastEventSequence must be >= 0 and endReason Finished, Abandoned or TimedOut.", 422);
        var result = await Gate("Complete", caller, id, request, key, ct);
        if (!result.IsSuccess) return new(default, result.Error);
        var body = result.Value.GetProperty("result").Deserialize<CompleteTrainingSessionResponse>(Json)!;
        return AuthResult<(int, CompleteTrainingSessionResponse)>.Ok((body.Status == "Completed" ? 200 : 202, body));
    }

    public async Task<AuthResult<TrainingResultView>> ResultAsync(LearnerCaller caller, Guid id, CancellationToken ct)
    {
        var result = await Gate("Result", caller, id, new { }, null, ct);
        return result.IsSuccess ? AuthResult<TrainingResultView>.Ok(result.Value.GetProperty("result").Deserialize<TrainingResultView>(Json)!) : new(default, result.Error);
    }

    public async Task<AuthResult<TrainingContinuation>> ContinuationAsync(LearnerCaller caller, Guid id, CancellationToken ct)
    {
        if (Signers() is not { } signers) return AuthResult<TrainingContinuation>.Fail("LEARNER_LAUNCH_UNAVAILABLE", "Learner continuation signing is not configured.", 503);
        var result = await Gate("Continuation", caller, id, new { continuationTtlSeconds = Ttl() }, null, ct);
        return result.IsSuccess ? AuthResult<TrainingContinuation>.Ok(Continuation(caller.ActorId, result.Value.GetProperty("result"), id, signers.Continuation)) : new(default, result.Error);
    }

    public async Task<AuthResult<IReadOnlyList<TrainingSessionView>>> ReconcileAsync(LearnerCaller caller, ReconcileTrainingRequest request, CancellationToken ct)
    {
        if ((request.SessionIds?.Count ?? 0) + (request.IdempotencyKeys?.Count ?? 0) is 0 or > 100 || request.IdempotencyKeys?.Any(k => k is null || k.Length is 0 or > 128) == true)
            return AuthResult<IReadOnlyList<TrainingSessionView>>.Fail("VALIDATION_ERROR", "Send 1-100 of your own session IDs or idempotency keys.", 400);
        var result = await Gate("Reconcile", caller, Guid.Empty, new { sessionIds = request.SessionIds ?? [], idempotencyKeys = request.IdempotencyKeys ?? [] }, null, ct);
        return result.IsSuccess ? AuthResult<IReadOnlyList<TrainingSessionView>>.Ok(result.Value.GetProperty("result").Deserialize<List<TrainingSessionView>>(Json)!) : new(default, result.Error);
    }

    internal static string? ValidateEvents(TrainingEventsRequest request)
    {
        if (request.Events is not { Count: >= 1 and <= 500 } events) return "Send 1-500 events.";
        if (events.Select(e => e?.EventId).Distinct().Count() != events.Count) return "eventId values must be unique in a batch.";
        foreach (var e in events)
        {
            if (e is null || e.EventId == Guid.Empty || e.Sequence < 1 || e.ElapsedMs < 0 || string.IsNullOrWhiteSpace(e.SchemaVersion) || e.SchemaVersion.Length > 32
                || string.IsNullOrWhiteSpace(e.Type) || e.Type.Length > 100 || e.Payload.ValueKind != JsonValueKind.Object || e.OccurredAt == default)
                return "Each event needs eventId, sequence >= 1, schemaVersion, type, occurredAt, elapsedMs >= 0 and an object payload.";
            // Metric inputs must be finite, non-negative numbers so the server can compute the rubric.
            var field = e.Type switch { "HazardExposure" => "amount", "Moved" => "distanceMeters", _ => null };
            if (field is not null && (!e.Payload.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0 || number > 1e9))
                return $"{e.Type} events require a finite non-negative payload.{field}.";
        }
        return null;
    }

    private int Ttl() => Math.Clamp(options.Value.ContinuationTtlDays, 1, 30) * 86400;
    private sealed record SignerSet(SigningCredentials Launch, SigningCredentials Continuation);
    private SignerSet? Signers()
    {
        var o = options.Value;
        if (!o.Enabled || Encoding.UTF8.GetByteCount(o.SigningKey) < 32 || Encoding.UTF8.GetByteCount(o.ContinuationSigningKey) < 32 || o.SigningKey == o.ContinuationSigningKey) return null;
        return new(new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.SigningKey)), SecurityAlgorithms.HmacSha256),
            new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.ContinuationSigningKey)), SecurityAlgorithms.HmacSha256));
    }

    private TrainingLaunch Launch(JsonElement launch, SigningCredentials credentials)
    {
        var o = options.Value;
        var session = launch.GetProperty("sessionId").GetGuid();var generation = launch.GetProperty("generation").GetInt32();
        var issued = launch.GetProperty("issuedAt").GetDateTimeOffset().UtcDateTime;var expires = launch.GetProperty("expiresAt").GetDateTimeOffset().UtcDateTime;
        var package = launch.GetProperty("package");
        var claims = new[]
        {
            new Claim("purpose", "learner_launch"), new Claim(JwtRegisteredClaimNames.Sub, launch.GetProperty("traineeId").GetString()!),
            new Claim("sid", launch.GetProperty("familyId").GetString()!), new Claim("session_id", session.ToString()),
            new Claim("grant_generation", generation.ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer32),
            new Claim("mode", launch.GetProperty("mode").GetString()!), new Claim("runtime_version", launch.GetProperty("runtimeVersion").GetString()!),
            new Claim("package_hash", package.GetProperty("packageHash").GetString() ?? ""), new Claim("manifest_hash", package.GetProperty("manifestHash").GetString() ?? ""),
            new Claim(JwtRegisteredClaimNames.Jti, $"{session}:{generation}")
        };
        return new(session, generation, issued, expires, new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(o.Issuer, o.Audience, claims, issued, expires, credentials)));
    }

    private TrainingContinuation Continuation(Guid actor, JsonElement continuation, Guid session, SigningCredentials credentials)
    {
        var o = options.Value;var expires = continuation.GetProperty("expiresAt").GetDateTimeOffset().UtcDateTime;
        var claims = new[]
        {
            new Claim("purpose", "learner_continuation"), new Claim(JwtRegisteredClaimNames.Sub, actor.ToString()), new Claim("role", "Trainee"),
            new Claim("session_id", session.ToString()), new Claim(JwtRegisteredClaimNames.Jti, continuation.GetProperty("id").GetString()!)
        };
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(o.ContinuationIssuer, o.ContinuationAudience, claims, expires.AddDays(-Math.Clamp(o.ContinuationTtlDays, 1, 30)), expires, credentials));
        return new(session, expires, token);
    }

    private static AuthResult<TrainingSessionView> View(AuthResult<JsonElement> result) =>
        result.IsSuccess ? AuthResult<TrainingSessionView>.Ok(result.Value.GetProperty("result").Deserialize<TrainingSessionView>(Json)!) : new(default, result.Error);

    private async Task<AuthResult<JsonElement>> Gate(string action, LearnerCaller caller, Guid resource, object input, string? key, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT learner_session_gate(@action,@actor,@family,@continuation,@resource,@input,@key)::text", (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("action", action); command.Parameters.AddWithValue("actor", caller.ActorId);
        command.Parameters.AddWithValue("family", NpgsqlDbType.Uuid, (object?)caller.FamilyId ?? DBNull.Value);
        command.Parameters.AddWithValue("continuation", NpgsqlDbType.Uuid, (object?)caller.ContinuationId ?? DBNull.Value);
        command.Parameters.AddWithValue("resource", resource);
        command.Parameters.AddWithValue("input", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(input, Json));
        command.Parameters.AddWithValue("key", NpgsqlDbType.Text, (object?)key ?? DBNull.Value);
        using var document = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);
        var value = document.RootElement;
        if (value.GetProperty("code").GetString() == "OK") return AuthResult<JsonElement>.Ok(value.Clone());
        var code = value.GetProperty("code").GetString()!;
        var message = code switch
        {
            "BUILDING_ACCESS_REQUIRED" => "Verify the Building participation code first.",
            "BUILDING_ENTITLEMENT_REQUIRED" => "The Building has no current paid service.",
            "LEARNER_LIMIT_REACHED" => "The Building has reached its learner limit for this service period.",
            "TRAINING_UNAVAILABLE" => "The Training is not published or not available now.",
            "CONTENT_APPROVAL_REQUIRED" => "The Training content is not approved for this release.",
            "RUNTIME_INCOMPATIBLE" => "The runtime does not support this package.",
            "RUBRIC_NOT_SUPPORTED" => "Assessment needs a rubric with server-computed metrics.",
            "MODE_NOT_ALLOWED" => "The Training does not allow this mode.",
            "PREPARATION_EXPIRED" => "Prepare the session again.",
            "SESSION_TERMINAL" => "The session already has its result.",
            "CONTINUATION_INVALID" => "The continuation is expired or replaced; sign in to obtain a new one.",
            "CONTINUATION_NOT_ALLOWED" => "Continuation only syncs a started session.",
            "RESULT_NOT_READY" => "The result is not available until every event up to completion is received.",
            _ => "Session request was rejected. Check the session state and inputs."
        };
        return AuthResult<JsonElement>.Fail(code, message, value.GetProperty("status").GetInt32());
    }
}

public sealed class BuildingQrCodes(Fire3DDbContext db) : IBuildingQrCodes
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Task<AuthResult<BuildingQrCreated>> CreateAsync(Guid actor, Guid family, Guid building, CreateBuildingQrRequest request, CancellationToken ct) => Issue("Create", actor, family, building, null, request, ct);
    public Task<AuthResult<BuildingQrCreated>> RotateAsync(Guid actor, Guid family, Guid building, Guid qr, CreateBuildingQrRequest request, CancellationToken ct) => Issue("Rotate", actor, family, building, qr, request, ct);
    public async Task<AuthResult<IReadOnlyList<BuildingQrView>>> ListAsync(Guid actor, Guid family, Guid building, CancellationToken ct)
    {
        var result = await Gate("List", actor, family, building, new { }, ct);
        return result.IsSuccess ? AuthResult<IReadOnlyList<BuildingQrView>>.Ok(result.Value.GetProperty("result").EnumerateArray()
            .Select(x => new BuildingQrView(x.GetProperty("id").GetGuid(), building, x.GetProperty("label").GetString(), x.GetProperty("status").GetString()!,
                x.GetProperty("createdAt").GetDateTimeOffset().UtcDateTime, x.GetProperty("revokedAt").ValueKind == JsonValueKind.Null ? null : x.GetProperty("revokedAt").GetDateTimeOffset().UtcDateTime,
                x.GetProperty("replacedBy").ValueKind == JsonValueKind.Null ? null : x.GetProperty("replacedBy").GetGuid())).ToArray()) : new(default, result.Error);
    }
    public async Task<AuthResult<BuildingQrView>> RevokeAsync(Guid actor, Guid family, Guid building, Guid qr, CancellationToken ct)
    {
        var result = await Gate("Revoke", actor, family, building, new { qrId = qr }, ct);
        return result.IsSuccess ? AuthResult<BuildingQrView>.Ok(result.Value.GetProperty("result").Deserialize<BuildingQrView>(Json)!) : new(default, result.Error);
    }
    public async Task<AuthResult<BuildingQrResolution>> ResolveAsync(Guid actor, Guid family, string token, CancellationToken ct)
    {
        if (token is not { Length: 43 } || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return AuthResult<BuildingQrResolution>.Fail("QR_NOT_FOUND", "QR code not found.", 404);
        var result = await Gate("Resolve", actor, family, Guid.Empty, new { tokenHash = Hash(token) }, ct);
        return result.IsSuccess ? AuthResult<BuildingQrResolution>.Ok(result.Value.GetProperty("result").Deserialize<BuildingQrResolution>(Json)!) : new(default, result.Error);
    }
    private async Task<AuthResult<BuildingQrCreated>> Issue(string action, Guid actor, Guid family, Guid building, Guid? qr, CreateBuildingQrRequest request, CancellationToken ct)
    {
        if (request.Label is { Length: > 255 }) return AuthResult<BuildingQrCreated>.Fail("VALIDATION_ERROR", "Label is at most 255 characters.", 400);
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var result = await Gate(action, actor, family, building, new { tokenHash = Hash(token), label = request.Label, qrId = qr }, ct);
        return result.IsSuccess ? AuthResult<BuildingQrCreated>.Ok(new(result.Value.GetProperty("result").Deserialize<BuildingQrView>(Json)!, token)) : new(default, result.Error);
    }
    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private async Task<AuthResult<JsonElement>> Gate(string action, Guid actor, Guid family, Guid building, object input, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT building_qr_gate(@action,@actor,@family,@building,@input)::text", (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.AddWithValue("action", action); command.Parameters.AddWithValue("actor", actor); command.Parameters.AddWithValue("family", family);
        command.Parameters.AddWithValue("building", building); command.Parameters.AddWithValue("input", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(input, Json));
        using var document = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);
        var value = document.RootElement;
        return value.GetProperty("code").GetString() == "OK" ? AuthResult<JsonElement>.Ok(value.Clone())
            : AuthResult<JsonElement>.Fail(value.GetProperty("code").GetString()!, "QR request was rejected.", value.GetProperty("status").GetInt32());
    }
}
