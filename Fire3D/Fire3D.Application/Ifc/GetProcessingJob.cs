using Fire3D.Application.Authentication;
using MediatR;
namespace Fire3D.Application.Ifc;
/// <summary>One worker attempt. EndReason says why it ended (see <see cref="ProcessingAttemptEnd"/>); Message is the worker's error.</summary>
public sealed record ProcessingAttemptResponse(Guid Id, int AttemptNumber, string Status, string ToolchainVersion,
    DateTime StartedAt, DateTime? FinishedAt, string? OutputHash, string? Message = null)
{
    public string? EndReason => ProcessingAttemptEnd.Reason(Status);
}
/// <summary>PreviousAttempt is the attempt before the current one (BE#239), so a client can say why the job was claimed again.</summary>
public sealed record ProcessingJobDetailResponse(ProcessingJobResponse Job, string InputHash,
    Guid? CurrentAttemptId, ProcessingAttemptResponse? CurrentAttempt, ProcessingAttemptResponse? PreviousAttempt = null);
/// <summary>
/// Why an attempt ended, from its status (BE#239): Completed (Succeeded), WorkerFailed (Failed: the worker reported an error, see Message),
/// LeaseExpired (Expired: the worker did not renew its lease in time, e.g. it restarted, lost its connection or stopped, so the job was
/// handed out again). Null while Running.
/// </summary>
public static class ProcessingAttemptEnd
{
    public static string? Reason(string? status) => status switch
    {
        "Succeeded" => "Completed",
        "Failed" => "WorkerFailed",
        "Expired" => "LeaseExpired",
        _ => null
    };
}
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
