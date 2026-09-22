using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;

namespace Fire3D.Application.Scenarios.Commands.RejectScenarioVersion;

public sealed record RejectScenarioVersionRequest(Guid ScenarioVersionId, Guid ValidationRunId, string ReviewMessage,
    Guid? AnnotationSetId = null);
public interface IScenarioReviewStore
{
    Task<AuthResult<Guid>> CreateRejectedReviewAsync(Guid actorId, Guid revisionId, Guid? organizationId,
        RejectScenarioVersionRequest request, CancellationToken ct);
}
public sealed record RejectScenarioVersionCommand(Guid ActorId, Guid RevisionId, RejectScenarioVersionRequest Request)
    : IRequest<AuthResult<Guid>>;

public sealed class RejectScenarioVersionCommandHandler(IAuthStore accounts, IScenarioReviewStore store)
    : IRequestHandler<RejectScenarioVersionCommand, AuthResult<Guid>>
{
    public async Task<AuthResult<Guid>> Handle(RejectScenarioVersionCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        var request = command.Request;
        if (command.RevisionId == Guid.Empty || request is null || request.ScenarioVersionId == Guid.Empty
            || request.ValidationRunId == Guid.Empty || string.IsNullOrWhiteSpace(request.ReviewMessage)
            || request.ReviewMessage.Trim().Length > 4000)
            return AuthResult<Guid>.Fail("VALIDATION_ERROR",
                "Revision, scenario version, validation run, and a review message up to 4000 characters are required.", 400);

        return await store.CreateRejectedReviewAsync(command.ActorId, command.RevisionId, scope.Value!.OrganizationId,
            request with { ReviewMessage = request.ReviewMessage.Trim() }, ct);
    }
}
