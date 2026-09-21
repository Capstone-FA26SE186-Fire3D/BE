using Fire3D.Application.Authentication;
using MediatR;
namespace Fire3D.Application.Ifc;
public sealed record ProcessingAttemptResponse(Guid Id, int AttemptNumber, string Status, string ToolchainVersion,
    DateTime StartedAt, DateTime? FinishedAt, string? OutputHash);
public sealed record ProcessingJobDetailResponse(ProcessingJobResponse Job, string InputHash,
    Guid? CurrentAttemptId, ProcessingAttemptResponse? CurrentAttempt);
public sealed record GetProcessingJobQuery(Guid ActorId, Guid JobId) : IRequest<AuthResult<ProcessingJobDetailResponse>>;
public sealed class GetProcessingJobHandler(IAuthStore accounts, IIfcReadStore store)
    : IRequestHandler<GetProcessingJobQuery, AuthResult<ProcessingJobDetailResponse>>
{
    public async Task<AuthResult<ProcessingJobDetailResponse>> Handle(GetProcessingJobQuery request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.JobId == Guid.Empty)
            return AuthResult<ProcessingJobDetailResponse>.Fail("VALIDATION_ERROR", "Job ID is required.", 400);
        var result = await store.GetJobAsync(request.JobId, scope.Value!.OrganizationId, ct);
        return result is null ? AuthResult<ProcessingJobDetailResponse>.Fail("NOT_FOUND", "Processing job not found.", 404)
            : AuthResult<ProcessingJobDetailResponse>.Ok(result);
    }
}
