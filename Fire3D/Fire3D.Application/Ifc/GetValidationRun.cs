using System.Text.Json;
using Fire3D.Application.Authentication;
using MediatR;
namespace Fire3D.Application.Ifc;
public sealed record ValidationRunResponse(Guid Id, Guid RevisionId, Guid ProcessingJobId, Guid ProcessingAttemptId,
    Guid? ArtifactId, Guid? ScenarioVersionId, string Scope, string ValidatorVersion, string Status,
    JsonElement Summary, DateTime? StartedAt, DateTime? FinishedAt, DateTime CreatedAt, string? Outcome = null);
public sealed record GetValidationRunQuery(Guid ActorId, Guid ValidationRunId) : IRequest<AuthResult<ValidationRunResponse>>;
/// <summary>One validation run of a revision, current or historical. IsCurrent is true only for the accepted current attempt of a Succeeded job;
/// a historical run never satisfies confirm-for-training, release or publish.</summary>
public sealed record ValidationRunHistoryItem(Guid Id, Guid RevisionId, Guid ProcessingJobId, string JobKind, Guid ProcessingAttemptId, string AttemptStatus,
    Guid? ArtifactId, Guid? ScenarioVersionId, string Scope, string ValidatorVersion, string Outcome, bool IsCurrent,
    DateTime? StartedAt, DateTime? FinishedAt, DateTime CreatedAt);
public sealed record ListValidationRunsQuery(Guid ActorId, Guid RevisionId, Guid? JobId = null, int Page = 1, int PageSize = 20)
    : IRequest<AuthResult<Fire3D.Application.Administration.PageResponse<ValidationRunHistoryItem>>>;
public sealed class ListValidationRunsHandler(IAuthStore accounts, IIfcReadStore store)
    : IRequestHandler<ListValidationRunsQuery, AuthResult<Fire3D.Application.Administration.PageResponse<ValidationRunHistoryItem>>>
{
    public async Task<AuthResult<Fire3D.Application.Administration.PageResponse<ValidationRunHistoryItem>>> Handle(ListValidationRunsQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.RevisionId == Guid.Empty || request.JobId == Guid.Empty || request.Page is < 1 or > 100000 || request.PageSize is < 1 or > 100)
            return AuthResult<Fire3D.Application.Administration.PageResponse<ValidationRunHistoryItem>>.Fail("VALIDATION_ERROR", "Revision ID, optional job ID and valid pagination are required.", 400);
        var result = await store.ListValidationRunsAsync(request.RevisionId, request.JobId, scope.Value!.OrganizationId, request.Page, request.PageSize, ct);
        return result is null ? AuthResult<Fire3D.Application.Administration.PageResponse<ValidationRunHistoryItem>>.Fail("NOT_FOUND", "Revision not found.", 404)
            : AuthResult<Fire3D.Application.Administration.PageResponse<ValidationRunHistoryItem>>.Ok(result);
    }
}
public sealed class GetValidationRunHandler(IAuthStore accounts, IIfcReadStore store)
    : IRequestHandler<GetValidationRunQuery, AuthResult<ValidationRunResponse>>
{
    public async Task<AuthResult<ValidationRunResponse>> Handle(GetValidationRunQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.ValidationRunId == Guid.Empty)
            return AuthResult<ValidationRunResponse>.Fail("VALIDATION_ERROR", "Validation run ID is required.", 400);
        var result = await store.GetValidationAsync(request.ValidationRunId, scope.Value!.OrganizationId, ct);
        return result is null ? AuthResult<ValidationRunResponse>.Fail("NOT_FOUND", "Validation run not found.", 404)
            : AuthResult<ValidationRunResponse>.Ok(result);
    }
}
