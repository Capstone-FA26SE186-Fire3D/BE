using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Application.Scenarios.Commands.UpdateScenarioDraft;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;
using Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog;
using MediatR;

namespace Fire3D.Application.Scenarios.Commands.SnapshotScenarioDraft;

public sealed record SnapshotScenarioDraftCommand(Guid ActorId, Guid DraftId, uint? ExpectedVersion = null, string? IdempotencyKey = null) : IRequest<AuthResult<Guid>>;

public sealed class SnapshotScenarioDraftHandler(IAuthStore accounts, IScenarioWriteStore store, IScenarioReadStore reads, IRuntimeCatalogReadStore catalog)
    : IRequestHandler<SnapshotScenarioDraftCommand, AuthResult<Guid>>
{
    public async Task<AuthResult<Guid>> Handle(SnapshotScenarioDraftCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        // Versioned drafts are fully validated before the gate; the gate re-checks revision references under lock.
        // A stale If-Match or replayed key is left to the gate so precondition and receipt semantics stay unchanged.
        var draft = await reads.GetScenarioDraftAsync(command.DraftId, scope.Value!.OrganizationId, ct);
        if (draft is not null && draft.Version == command.ExpectedVersion
            && await VersionedDraftValidation.ValidateAsync(draft.State, draft.RevisionId, reads, catalog, ct) is { } issues)
        {
            if (issues.Any(x => x.Code == "EDITOR_SCHEMA_VERSION_UNSUPPORTED")) return DraftStateInput.Unsupported<Guid>();
            if (issues.Count > 0) return DraftStateInput.Invalid<Guid>(issues);
        }
        return await store.SnapshotScenarioDraftAsync(command.ActorId, command.DraftId, scope.Value!.OrganizationId, ct, command.IdempotencyKey, command.ExpectedVersion);
    }
}
