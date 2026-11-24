using Fire3D.Application.Authentication;

namespace Fire3D.Application.Ifc;

/// <summary>
/// One version of the IFC processing policy (BE#219): how many new Geometry jobs an Organization may start per UTC day. OrganizationId
/// null is the default for every Organization; an Organization row overrides it (DailyLimit null there = back to the default). Rows are
/// immutable; the newest one in effect wins. PolicyKind is DailyGeometryRuns (room for sold extra runs later).
/// </summary>
public sealed record IfcProcessingPolicyResponse(Guid Id, Guid? OrganizationId, string PolicyKind, int? DailyLimit, DateTimeOffset EffectiveFrom,
    string? Note, Guid CreatedBy, DateTimeOffset CreatedAt);
/// <summary>The limit in effect. Source: Organization (override), Default (platform default) or Configuration (IfcProcessing:DailyLimitPerOrganization, no policy yet).</summary>
public sealed record IfcProcessingLimit(int DailyLimit, string Source, Guid? PolicyId);
public sealed record IfcProcessingPolicyPage(IReadOnlyList<IfcProcessingPolicyResponse> Items, int TotalCount, int Page, int PageSize, IfcProcessingLimit EffectiveDefault);
/// <summary>Limit and use today (UTC) of one Organization; ResetsAt is the next UTC midnight.</summary>
public sealed record IfcProcessingUsage(Guid OrganizationId, int DailyLimit, string Source, Guid? PolicyId, int Used, int Remaining, DateTimeOffset ResetsAt);
/// <summary>
/// New policy version. OrganizationId null sets the default (DailyLimit required); with an Organization, DailyLimit null removes its
/// override. DailyLimit 0-100000 (0 stops new processing). EffectiveFrom defaults to now and cannot be in the past.
/// </summary>
public sealed record CreateIfcProcessingPolicyRequest(Guid? OrganizationId, int? DailyLimit, DateTimeOffset? EffectiveFrom = null, string? Note = null);

public interface IIfcProcessingPolicies
{
    Task<AuthResult<IfcProcessingPolicyPage>> ListAsync(Guid actor, Guid? family, Guid? organizationId, string? scope, int page, int pageSize, CancellationToken ct);
    Task<AuthResult<IfcProcessingPolicyResponse>> CreateAsync(Guid actor, Guid? family, CreateIfcProcessingPolicyRequest request, CancellationToken ct);
    /// <summary>organizationId null: the caller's own Organization (OrganizationUser); PlatformAdmin passes one.</summary>
    Task<AuthResult<IfcProcessingUsage>> UsageAsync(Guid actor, Guid? family, Guid? organizationId, CancellationToken ct);
}
