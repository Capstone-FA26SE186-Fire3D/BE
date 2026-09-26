using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios.Commands.PreparePlaytestSession;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Scenarios;

/// <summary>
/// Temporary runtime containment while the Building entitlement and trial gate is
/// migrated. It deliberately refuses new playtests instead of falling back to an
/// unscoped or empty entitlement.
/// </summary>
public sealed class FailClosedPlaytestWriteStore(Fire3DDbContext db) : IPlaytestWriteStore
{
    public Task<AuthResult<Guid>> PreparePlaytestAsync(
        Guid actorId,
        Guid buildingId,
        Guid scenarioId,
        Guid organizationId,
        PreparePlaytestRequest request,
        CancellationToken ct) =>
        Task.FromResult(AuthResult<Guid>.Fail(
            "ENTITLEMENT_UNAVAILABLE",
            "Playtest is unavailable until the Building entitlement gate is configured.",
            503));

    public async Task<AuthResult<bool>> StartPlaytestAsync(
        Guid actorId,
        Guid playtestId,
        Guid organizationId,
        CancellationToken ct)
    {
        var ownsSession = await db.PlaytestSessions.AsNoTracking().AnyAsync(
            session => session.Id == playtestId
                && session.CreatedBy == actorId
                && (organizationId == Guid.Empty || session.OrganizationId == organizationId),
            ct);
        if (!ownsSession)
            return AuthResult<bool>.Fail("NOT_FOUND", "Playtest session not found or access denied.", 404);

        return await new PlaytestWriteStore(db).StartPlaytestAsync(actorId, playtestId, organizationId, ct);
    }
}
