using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace Fire3D.Infrastructure.Ifc;

/// <summary>IFC processing policies and usage through ifc_processing_policy_gate (BE#219).</summary>
public sealed class IfcProcessingPolicies(Fire3DDbContext db, IConfiguration configuration) : IIfcProcessingPolicies
{
    private int Fallback => ProcessingRuntimeStore.DailyLimit(configuration);

    public async Task<AuthResult<IfcProcessingPolicyPage>> ListAsync(Guid actor, Guid? family, Guid? organizationId, string? scope, int page, int pageSize, CancellationToken ct)
    {
        if (scope is not null and not ("All" or "Default") || page < 1 || pageSize is < 1 or > 100)
            return AuthResult<IfcProcessingPolicyPage>.Fail("VALIDATION_ERROR", "scope is All or Default; page >= 1; pageSize 1-100.", 400);
        return Map<IfcProcessingPolicyPage>(await JsonCommandGate.Execute(db, "ifc_processing_policy_gate", "ListPolicies", actor, family, null,
            new { organizationId, scope, page, pageSize, fallback = Fallback }, null, null, ct));
    }

    public async Task<AuthResult<IfcProcessingPolicyResponse>> CreateAsync(Guid actor, Guid? family, CreateIfcProcessingPolicyRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.OrganizationId is null && request.DailyLimit is null) errors["dailyLimit"] = ["The default policy needs a dailyLimit."];
        if (request.DailyLimit is < 0 or > 100_000) errors["dailyLimit"] = ["dailyLimit must be 0-100000 (0 stops new processing)."];
        if (request.EffectiveFrom is { } from && from < DateTimeOffset.UtcNow.AddMinutes(-1)) errors["effectiveFrom"] = ["effectiveFrom cannot be in the past."];
        if (request.Note is { } note && (string.IsNullOrWhiteSpace(note) || note.Trim().Length > 500)) errors["note"] = ["note must contain 1-500 characters."];
        if (errors.Count > 0) return AuthResult<IfcProcessingPolicyResponse>.Fail("VALIDATION_ERROR", "IFC processing policy is not valid.", 400, errors);
        return Map<IfcProcessingPolicyResponse>(await JsonCommandGate.Execute(db, "ifc_processing_policy_gate", "CreatePolicy", actor, family, null,
            new { organizationId = request.OrganizationId, dailyLimit = request.DailyLimit, effectiveFrom = request.EffectiveFrom, note = request.Note?.Trim() }, null, null, ct));
    }

    public async Task<AuthResult<IfcProcessingUsage>> UsageAsync(Guid actor, Guid? family, Guid? organizationId, CancellationToken ct) =>
        Map<IfcProcessingUsage>(await JsonCommandGate.Execute(db, "ifc_processing_policy_gate", "Usage", actor, family, organizationId, new { fallback = Fallback }, null, null, ct));

    private static AuthResult<T> Map<T>(AuthResult<JsonElement> result) =>
        result.IsSuccess ? AuthResult<T>.Ok(result.Value.Deserialize<T>(JsonCommandGate.Json)!) : new(default, result.Error);
}
