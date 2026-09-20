using Fire3D.Application.Authentication;
using Fire3D.Application.Administration;
using MediatR;
namespace Fire3D.Application.Ifc;
public sealed record JobQaResponse(Guid JobId, Guid? CurrentAttemptId, PageResponse<ValidationRunResponse> ValidationRuns);
public sealed record GetJobQaQuery(Guid ActorId, Guid JobId, int Page = 1, int PageSize = 20) : IRequest<AuthResult<JobQaResponse>>;
public sealed class GetJobQaHandler(IAuthStore accounts, IIfcReadStore store) : IRequestHandler<GetJobQaQuery, AuthResult<JobQaResponse>>
{
    public async Task<AuthResult<JobQaResponse>> Handle(GetJobQaQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.JobId == Guid.Empty || request.Page is < 1 or > 100000 || request.PageSize is < 1 or > 100)
            return AuthResult<JobQaResponse>.Fail("VALIDATION_ERROR", "Job ID and valid pagination are required.", 400);
        var result = await store.GetJobQaAsync(request.JobId, scope.Value!.OrganizationId, request.Page, request.PageSize, ct);
        return result is null ? AuthResult<JobQaResponse>.Fail("NOT_FOUND", "Processing job not found.", 404) : AuthResult<JobQaResponse>.Ok(result);
    }
}
