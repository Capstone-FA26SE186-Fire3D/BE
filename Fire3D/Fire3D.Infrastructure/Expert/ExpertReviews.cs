using System.Text.Json;
using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using Fire3D.Application.Expert;
using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Persistence;

namespace Fire3D.Infrastructure.Expert;

/// <summary>FireSafetyExpert queue and decisions through expert_review_gate (BE#211).</summary>
public sealed class ExpertReviews(Fire3DDbContext db, IEditorPreviewStore previews, IPreviewDownloadSigner signer) : IExpertReviews
{
    public async Task<AuthResult<PageResponse<ExpertReviewItem>>> ListAsync(Guid actor, Guid? family, ExpertReviewFilter filter, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (filter.Kind is not null and not ("Scenario" or "Infrastructure")) errors["kind"] = ["Use Scenario or Infrastructure."];
        if (filter.Status is not null and not ("Submitted" or "Approved" or "Rejected")) errors["status"] = ["Use Submitted, Approved or Rejected."];
        if (filter.Page < 1) errors["page"] = ["Page must be at least 1."];
        if (filter.PageSize is < 1 or > 100) errors["pageSize"] = ["Page size must be 1-100."];
        if (errors.Count > 0) return AuthResult<PageResponse<ExpertReviewItem>>.Fail("VALIDATION_ERROR", "Review filters are invalid.", 400, errors);
        var result = await JsonCommandGate.Execute(db, "expert_review_gate", "List", actor, family, null,
            new { kind = filter.Kind, status = filter.Status, page = filter.Page, pageSize = filter.PageSize }, null, null, ct);
        return result.IsSuccess ? AuthResult<PageResponse<ExpertReviewItem>>.Ok(result.Value.Deserialize<PageResponse<ExpertReviewItem>>(JsonCommandGate.Json)!) : new(default, result.Error);
    }

    public async Task<AuthResult<ExpertReviewDetail>> GetAsync(Guid actor, Guid? family, Guid reviewId, CancellationToken ct)
    {
        var result = await JsonCommandGate.Execute(db, "expert_review_gate", "Get", actor, family, reviewId, new { }, null, null, ct);
        return result.IsSuccess ? AuthResult<ExpertReviewDetail>.Ok(result.Value.Deserialize<ExpertReviewDetail>(JsonCommandGate.Json)!) : new(default, result.Error);
    }

    public async Task<AuthResult<EditorPreviewResponse>> PreviewAsync(Guid actor, Guid? family, Guid reviewId, CancellationToken ct)
    {
        var target = await JsonCommandGate.Execute(db, "expert_review_gate", "Preview", actor, family, reviewId, new { }, null, null, ct);
        if (!target.IsSuccess) return new(default, target.Error);
        var building = target.Value.GetProperty("buildingId").GetGuid();
        var revision = target.Value.GetProperty("revisionId").GetGuid();
        // The gate has already limited the expert to this review's revision, so the read is not scoped to a tenant.
        var source = await previews.ReadAsync(building, revision, null, ct);
        if (source is null) return AuthResult<EditorPreviewResponse>.Fail("NOT_FOUND", "The revision of this review has no preview.", 404);
        return await EditorPreviewBuilder.BuildAsync(source, signer, ct);
    }

    public async Task<AuthResult<ExpertReviewItem>> DecideAsync(Guid actor, Guid? family, Guid reviewId, bool approve, string? reason, long expectedRevision, string key, CancellationToken ct)
    {
        object input = approve ? new { } : new { reason = reason?.Trim() };
        var result = await JsonCommandGate.Execute(db, "expert_review_gate", approve ? "Approve" : "Reject", actor, family, reviewId, input, key, expectedRevision, ct);
        return result.IsSuccess ? AuthResult<ExpertReviewItem>.Ok(result.Value.Deserialize<ExpertReviewItem>(JsonCommandGate.Json)!) : new(default, result.Error);
    }
}
