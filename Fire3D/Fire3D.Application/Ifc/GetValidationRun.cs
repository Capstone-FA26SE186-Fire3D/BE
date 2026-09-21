using System.Text.Json;
using Fire3D.Application.Authentication;
using MediatR;
namespace Fire3D.Application.Ifc;
public sealed record ValidationRunResponse(Guid Id, Guid RevisionId, Guid ProcessingJobId, Guid ProcessingAttemptId,
    Guid? ArtifactId, Guid? ScenarioVersionId, string Scope, string ValidatorVersion, string Status,
    JsonElement Summary, DateTime? StartedAt, DateTime? FinishedAt, DateTime CreatedAt);
public sealed record GetValidationRunQuery(Guid ActorId, Guid ValidationRunId) : IRequest<AuthResult<ValidationRunResponse>>;
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
