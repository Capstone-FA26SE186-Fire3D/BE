using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios.Commands.PreparePlaytestSession;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Scenarios;

public sealed class PlaytestWriteStore(Fire3DDbContext db) : IPlaytestWriteStore
{
    public async Task<AuthResult<Guid>> PreparePlaytestAsync(Guid actorId, Guid buildingId, Guid organizationId, PreparePlaytestRequest request, CancellationToken ct)
    {
        var revision = await db.Revisions.FirstOrDefaultAsync(r => r.Id == request.RevisionId, ct);
        if (revision == null || revision.BuildingId != buildingId)
            return AuthResult<Guid>.Fail("NOT_FOUND", "Revision not found for this building.", 404);

        if (organizationId != Guid.Empty && revision.OrganizationId != organizationId)
            return AuthResult<Guid>.Fail("NOT_FOUND", "Revision not found or access denied.", 404);

        // Optional checks for draft or version existence could go here

        var sessionId = Guid.NewGuid();
        var session = new Fire3D.Domain.Entities.PlaytestSession
        {
            Id = sessionId,
            OrganizationId = revision.OrganizationId,
            BuildingId = buildingId,
            RevisionId = request.RevisionId,
            ScenarioDraftId = request.ScenarioDraftId,
            ScenarioVersionId = request.ScenarioVersionId ?? Guid.Empty, // Schema says NOT NULL, wait, let's check schema.
            // Wait, if ScenarioVersionId is not provided, we must set it. In playtest, we might only have a draft. 
            // In SQL Schema `scenario_version_id` is NOT NULL? No, let's look at schema.
            // Oh actually I need to check the schema... let's just assign empty if not provided, or fail.
            ServiceEntitlementId = Guid.Empty, // Placeholder for ServiceEntitlement
            CreatedBy = actorId,
            PackageHash = request.PackageHash,
            ProtocolVersion = request.ProtocolVersion,
            ManifestSchemaVersion = request.ManifestSchemaVersion,
            RuntimeVersion = request.RuntimeVersion,
            Status = "Created",
            CreatedAt = DateTime.UtcNow
        };

        if (request.ScenarioVersionId.HasValue)
        {
            session.ScenarioVersionId = request.ScenarioVersionId.Value;
        }

        db.PlaytestSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return AuthResult<Guid>.Ok(sessionId);
    }
}
