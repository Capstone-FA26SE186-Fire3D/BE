using Fire3D.Application.Authentication;
using Fire3D.Application.Releases;
using Fire3D.Infrastructure.Persistence;

namespace Fire3D.Infrastructure.Releases;

/// <summary>
/// Temporary runtime containment while the publish gate is migrated. Publishing
/// must not bypass Building entitlement, validation, issue, and compatibility checks.
/// </summary>
public sealed class FailClosedReleaseStore(Fire3DDbContext db, TimeProvider clock) : IReleaseStore
{
    private readonly ReleaseWriteStore inner = new(db, clock);

    public Task<AuthResult<ReleaseResponse>> BuildAsync(
        Guid actorId,
        Guid? organizationId,
        BuildReleaseRequest request,
        CancellationToken ct) => inner.BuildAsync(actorId, organizationId, request, ct);

    public Task<ReleaseResponse?> GetAsync(Guid releaseId, Guid? organizationId, CancellationToken ct) =>
        inner.GetAsync(releaseId, organizationId, ct);

    public Task<AuthResult<bool>> PublishAsync(Guid actorId, Guid releaseId, Guid? organizationId, CancellationToken ct) =>
        Task.FromResult(AuthResult<bool>.Fail(
            "PUBLISH_GATE_UNAVAILABLE",
            "Publishing is unavailable until the Building entitlement and readiness gate is configured.",
            503));

    public Task<AuthResult<bool>> RevokeAsync(
        Guid actorId,
        Guid releaseId,
        Guid? organizationId,
        string reason,
        CancellationToken ct) => inner.RevokeAsync(actorId, releaseId, organizationId, reason, ct);
}
