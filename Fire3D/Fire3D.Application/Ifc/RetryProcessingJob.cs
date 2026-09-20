using Fire3D.Application.Authentication;
using MediatR;
namespace Fire3D.Application.Ifc;
public sealed record RetryProcessingJobRequest(Guid RequestId, string Reason);
public sealed record RetryProcessingJobResponse(Guid JobId, string Outcome);
public interface IIfcWriteStore
{
    Task<AuthResult<RetryProcessingJobResponse>> RetryJobAsync(Guid actorId, Guid jobId, Guid requestId, string reason, CancellationToken ct);
    Task<AuthResult<bool>> InitiateUploadAsync(Guid actorId, Guid buildingId, Guid revisionId, string versionLabel, Guid? actorTenantId, CancellationToken ct);
    Task<AuthResult<bool>> FinalizeUploadAsync(Guid actorId, Guid revisionId, Fire3D.Application.Ifc.Commands.FinalizeUpload.FinalizeIfcUploadRequest request, Guid? actorTenantId, CancellationToken ct);
    Task<AuthResult<Guid>> ProcessRevisionAsync(Guid actorId, Guid revisionId, Guid? actorTenantId, CancellationToken ct);
    Task<AuthResult<bool>> ConfirmForTrainingAsync(Guid actorId, Guid revisionId, Guid? actorTenantId, CancellationToken ct);
}
public sealed record RetryProcessingJobCommand(Guid ActorId, Guid JobId, RetryProcessingJobRequest Request)
    : IRequest<AuthResult<RetryProcessingJobResponse>>;
public sealed class RetryProcessingJobHandler(IAuthStore accounts, IIfcWriteStore store)
    : IRequestHandler<RetryProcessingJobCommand, AuthResult<RetryProcessingJobResponse>>
{
    public async Task<AuthResult<RetryProcessingJobResponse>> Handle(RetryProcessingJobCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        var request = command.Request;
        if (command.JobId == Guid.Empty || request is null || request.RequestId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 1000)
            return AuthResult<RetryProcessingJobResponse>.Fail("VALIDATION_ERROR", "Job ID, requestId and reason (1-1000 characters) are required.", 400);
        return await store.RetryJobAsync(command.ActorId, command.JobId, request.RequestId, request.Reason.Trim(), ct);
    }
}
