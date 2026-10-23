using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;

namespace Fire3D.Application.Scenarios.Queries.GetOrganizationWorkInProgress;

/// <summary>Kind is ScenarioDraft. Drafts stay mutable (versions are immutable snapshots), so resuming never edits an approved version.
/// LatestVersionNumber/LatestVersionReviewStatus describe the scenario's newest snapshot and are null when it has none.
/// UpdatedAt is the draft's last save.</summary>
public sealed record WorkInProgressItemResponse(string Kind, Guid BuildingId, string BuildingName, Guid ScenarioId, string ScenarioName,
    Guid DraftId, Guid RevisionId, int? LatestVersionNumber, string? LatestVersionReviewStatus, DateTime UpdatedAt);
public sealed record OrganizationWorkInProgressResponse(IReadOnlyList<WorkInProgressItemResponse> Items);

public sealed record GetOrganizationWorkInProgressQuery(Guid ActorId, Guid? SessionFamilyId, int Limit = 1)
    : IRequest<AuthResult<OrganizationWorkInProgressResponse>>;

public sealed class GetOrganizationWorkInProgressQueryHandler(IAuthStore accounts, IScenarioReadStore store, IScenarioReviewQueries reviews)
    : IRequestHandler<GetOrganizationWorkInProgressQuery, AuthResult<OrganizationWorkInProgressResponse>>
{
    public const int MaxLimit = 5;

    public async Task<AuthResult<OrganizationWorkInProgressResponse>> Handle(GetOrganizationWorkInProgressQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        // "me" has no tenant for PlatformAdmin.
        if (scope.Value!.OrganizationId is not Guid organizationId)
            return AuthResult<OrganizationWorkInProgressResponse>.Fail("FORBIDDEN", "OrganizationUser is required.", 403);
        if (request.Limit is < 1 or > MaxLimit)
            return AuthResult<OrganizationWorkInProgressResponse>.Fail("VALIDATION_ERROR", "limit must be 1–5.", 400,
                new Dictionary<string, string[]> { ["limit"] = ["Use 1–5."] });

        var drafts = await store.ListRecentDraftsAsync(organizationId, request.Limit, ct);
        var versionIds = drafts.Where(d => d.LatestVersionId.HasValue).Select(d => d.LatestVersionId!.Value).Distinct().ToArray();
        var statuses = new Dictionary<Guid, string>();
        if (versionIds.Length > 0)
        {
            var states = await reviews.StatesAsync(request.ActorId, request.SessionFamilyId, versionIds, ct);
            if (!states.IsSuccess) return new(default, states.Error);
            foreach (var state in states.Value!) statuses[state.ScenarioVersionId] = state.ReviewStatus;
        }
        return AuthResult<OrganizationWorkInProgressResponse>.Ok(new(drafts.Select(d => new WorkInProgressItemResponse("ScenarioDraft",
            d.BuildingId, d.BuildingName, d.ScenarioId, d.ScenarioName, d.DraftId, d.RevisionId, d.LatestVersionNumber,
            d.LatestVersionId is Guid version ? statuses.GetValueOrDefault(version) : null, d.UpdatedAt)).ToList()));
    }
}
