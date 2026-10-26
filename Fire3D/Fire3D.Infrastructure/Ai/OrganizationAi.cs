using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Ai;
using Fire3D.Application.Authentication;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Fire3D.Infrastructure.Ai;

public sealed class OrganizationAiGates(Fire3DDbContext db) : IOrganizationAiGates
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<AuthResult<JsonElement>> OrganizationAsync(string action, Guid actor, Guid family, Guid resource, object input, string? key, CancellationToken ct) =>
        Result(Run("SELECT ai_org_gate(@action,@actor,@family,@resource,@input,@key)::text", ct, ("action", action), ("actor", actor), ("family", family), ("resource", resource), ("input", input), ("key", key)));
    public Task<AuthResult<JsonElement>> OrganizationRequestsAsync(Guid actor, Guid family, object input, CancellationToken ct) =>
        Result(Run("SELECT ai_org_request_list_gate(@actor,@family,@input)::text", ct, ("actor", actor), ("family", family), ("input", input)));
    public Task<AuthResult<JsonElement>> TraineeAsync(string action, Guid actor, Guid family, Guid resource, object input, string? key, CancellationToken ct) =>
        Result(Run("SELECT ai_trainee_gate(@action,@actor,@family,@resource,@input,@key)::text", ct, ("action", action), ("actor", actor), ("family", family), ("resource", resource), ("input", input), ("key", key)));
    public Task<AuthResult<JsonElement>> AdminAsync(string action, Guid actor, Guid family, Guid resource, object input, string? key, long? expected, CancellationToken ct) =>
        Result(Run("SELECT ai_admin_gate(@action,@actor,@family,@resource,@input,@key,@expected)::text", ct, ("action", action), ("actor", actor), ("family", family), ("resource", resource), ("input", input), ("key", key), ("expected", expected)));
    public Task<JsonElement> WorkerAsync(string action, string worker, Guid? request, Guid? lease, object input, CancellationToken ct) =>
        Run("SELECT ai_worker_gate(@action,@worker,@request,@lease,@input)::text", ct, ("action", action), ("worker", worker), ("request", request), ("lease", lease), ("input", input));

    private async Task<JsonElement> Run(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)db.Database.GetDbConnection());
        foreach (var (name, value) in parameters)
            command.Parameters.Add(value switch
            {
                Guid g => new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = g },
                null when name is "request" or "lease" or "resource" or "actor" or "family" => new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = DBNull.Value },
                long l => new NpgsqlParameter(name, NpgsqlDbType.Bigint) { Value = l },
                null when name == "expected" => new NpgsqlParameter(name, NpgsqlDbType.Bigint) { Value = DBNull.Value },
                string s => new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = s },
                null => new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = DBNull.Value },
                _ => new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(value, Json) }
            });
        using var document = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);
        return document.RootElement.Clone();
    }

    private static async Task<AuthResult<JsonElement>> Result(Task<JsonElement> pending)
    {
        var value = await pending;
        var code = value.GetProperty("code").GetString()!;
        if (code == "OK") return AuthResult<JsonElement>.Ok(value.GetProperty("result").Clone());
        var message = code switch
        {
            "AI_POLICY_UNAVAILABLE" => "No AI policy is active for this operation.",
            "AI_QUOTA_EXHAUSTED" => "Not enough AI quota for this request; buy a top-up or wait for the next period.",
            "AI_SOURCE_NOT_ALLOWED" => "Use only sources listed by GET /api/ai/organization/sources.",
            "AI_NO_SOURCES" => "No approved source is available for AI yet.",
            "AI_TOO_MANY_SOURCES" => "Too many sources for the active policy.",
            "AI_INPUT_TOO_LARGE" => "The prompt is longer than the active policy allows.",
            "AI_RECONCILE_NOT_ALLOWED" => "Only requests in NeedsReconcile can be reconciled.",
            "AI_POLICY_OVERLAP" => "Another policy for this operation overlaps the period.",
            "AI_QUOTA_UNIT_UNSUPPORTED" => "The AI service accepts only the quota units tokens and requests.",
            "AI_POLICY_ENDED" => "The policy has already ended.",
            "AI_POLICY_END_INVALID" => "effectiveUntil must not be in the past, must be after effectiveFrom and earlier than the current end.",
            "KNOWLEDGE_SOURCE_EXISTS" => "This source version is already registered.",
            "KNOWLEDGE_SOURCE_STATE_CONFLICT" => "The source is not in a state that allows this action.",
            "AI_REVISION_NOT_READY" => "The Building has no revision with accepted geometry for a scenario draft.",
            "AI_BLOCKED_DURING_ASSESSMENT" => "AI is not available while an Assessment attempt is open.",
            "AI_CONTEXT_NOT_FOUND" => "The Learn post, Training or result is not available to you.",
            "AI_DAILY_QUOTA_EXHAUSTED" => "Today's AI allowance is used up; it resets at the start of the next day.",
            _ => "AI request was rejected."
        };
        return AuthResult<JsonElement>.Fail(code, message, value.GetProperty("status").GetInt32());
    }
}

/// <summary>
/// FastAPI adapter contract v2: POST /v2/organization/{scenario-draft|answer}, POST /v2/trainee/answer and
/// GET /v2/requests/{requestId}. The body is the payload the gate pins at claim time (docs/organization-ai.md); the stable
/// request id is sent as X-Request-Id and request_id so the service can deduplicate and answer lookups.
/// </summary>
public sealed class FastApiAiClient(IHttpClientFactory clients, IOptions<OrganizationAiOptions> options) : IAiProviderClient
{
    public const string ClientName = "fet3d-ai";

    public async Task<AiCallResult> DispatchAsync(JsonElement claim, CancellationToken ct)
    {
        var id = claim.GetProperty("requestId").GetGuid();
        var path = claim.GetProperty("operation").GetString() switch
        {
            "ScenarioDraft" => "v2/organization/scenario-draft",
            "TraineeAnswer" => "v2/trainee/answer",
            _ => "v2/organization/answer"
        };
        return await Send(HttpMethod.Post, path, id, JsonNode.Parse(claim.GetProperty("payload").GetRawText())!.AsObject(), lookup: false, ct);
    }

    public Task<AiCallResult> LookupAsync(Guid requestId, CancellationToken ct) => Send(HttpMethod.Get, $"v2/requests/{requestId}", requestId, null, lookup: true, ct);

    private async Task<AiCallResult> Send(HttpMethod method, string path, Guid id, JsonObject? body, bool lookup, CancellationToken ct)
    {
        var o = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
        using var request = new HttpRequestMessage(method, new Uri(new Uri(o.BaseUrl!.TrimEnd('/') + "/"), path));
        request.Headers.Add("X-Request-Id", id.ToString());
        if (!string.IsNullOrEmpty(o.ApiKey)) request.Headers.Authorization = new("Bearer", o.ApiKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        HttpResponseMessage response;
        try { response = await clients.CreateClient(ClientName).SendAsync(request, timeout.Token); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(AiCallStatus.Unknown, Reason: "timeout"); }
        // No connection was established, so the request never reached the service.
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError)
        { return new(AiCallStatus.NotDelivered, Reason: "connection"); }
        catch (HttpRequestException) { return new(AiCallStatus.Unknown, Reason: "transport"); }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.OK)
            {
                JsonObject? parsed = null;
                try { parsed = await response.Content.ReadFromJsonAsync<JsonObject>(timeout.Token); } catch (JsonException) { }
                return new(AiCallStatus.Completed, parsed ?? new JsonObject());
            }
            if (lookup) return response.StatusCode == HttpStatusCode.NotFound ? new(AiCallStatus.NotFound) : new(AiCallStatus.Unknown, Reason: ((int)response.StatusCode).ToString());
            if ((int)response.StatusCode is 400 or 422)
            {
                // The refusal may name the pinned input it could not use (hash mismatch, source or revision unavailable).
                JsonObject? refusal = null;
                try { refusal = await response.Content.ReadFromJsonAsync<JsonObject>(timeout.Token); } catch (JsonException) { } catch (NotSupportedException) { }
                return new(AiCallStatus.Rejected, refusal, ((int)response.StatusCode).ToString());
            }
            return (int)response.StatusCode switch
            {
                401 or 403 or 404 or 429 or 503 => new(AiCallStatus.NotDelivered, Reason: ((int)response.StatusCode).ToString()),
                _ => new(AiCallStatus.Unknown, Reason: ((int)response.StatusCode).ToString())
            };
        }
    }
}

/// <summary>Claims one request at a time, calls the adapter outside any transaction and settles, releases or parks it for reconciliation.</summary>
public sealed class OrganizationAiWorker(IServiceScopeFactory scopes, IOptions<OrganizationAiOptions> options, ILogger<OrganizationAiWorker> log) : BackgroundService
{
    private readonly string worker = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Configured)
        {
            if (options.Value.Problem is { } problem) log.LogError("Organization AI is enabled but stays off: {Problem}", problem);
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollSeconds));
        do
        {
            try { while (await RunOnceAsync(stoppingToken)) { } }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning("Organization AI worker failed: {ErrorType}", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var gates = scope.ServiceProvider.GetRequiredService<IOrganizationAiGates>();
        var provider = scope.ServiceProvider.GetRequiredService<IAiProviderClient>();
        var o = options.Value;
        var claimed = await gates.WorkerAsync("Claim", worker, null, null, new { leaseSeconds = o.LeaseSeconds }, ct);
        if (claimed.GetProperty("code").GetString() != "OK") return false;
        var claim = claimed.GetProperty("result");
        var id = claim.GetProperty("requestId").GetGuid();
        var lease = claim.GetProperty("leaseToken").GetGuid();
        var dispatch = claim.GetProperty("phase").GetString() == "Dispatch";
        var call = dispatch ? await provider.DispatchAsync(claim, ct) : await provider.LookupAsync(id, ct);
        var (action, input) = call.Status switch
        {
            AiCallStatus.Completed => ("Complete", (object)OrganizationAiInput.Completion(claim.GetProperty("operation").GetString()!, id, call.Body!).Completion),
            AiCallStatus.NotDelivered when dispatch => ("Retry", new { maxAttempts = o.MaxAttempts, backoffSeconds = o.BackoffSeconds, reason = "AI_PROVIDER_NOT_DELIVERED" }),
            AiCallStatus.Rejected when dispatch => ("Reject", new { code = call.Body?["code"] is JsonValue c && c.TryGetValue<string>(out var code) ? code : null }),
            AiCallStatus.NotFound when !dispatch => ("NotFound", new { }),
            _ => ("Unknown", (object)new { })
        };
        var settled = await gates.WorkerAsync(action, worker, id, lease, input, ct);
        log.LogInformation("Organization AI request {RequestId}: Phase={Phase}, Call={CallStatus}, Reason={Reason}, Action={Action}, Result={Code}/{Status}",
            id, dispatch ? "Dispatch" : "Lookup", call.Status, call.Reason, action, settled.GetProperty("code").GetString(),
            settled.TryGetProperty("result", out var r) && r.TryGetProperty("status", out var s) ? s.GetString() : null);
        return true;
    }
}
