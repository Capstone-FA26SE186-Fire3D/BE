using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios.Commands.PreparePlaytestSession;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Fire3D.Infrastructure.Scenarios;

public sealed class PlaytestWriteStore(Fire3DDbContext db) : IPlaytestWriteStore
{
    public async Task<AuthResult<Guid>> PreparePlaytestAsync(Guid actorId, Guid buildingId, Guid scenarioId, Guid organizationId, PreparePlaytestRequest request, CancellationToken ct)
    {
        var revision = await db.Revisions.Include(r => r.Building).FirstOrDefaultAsync(r => r.Id == request.RevisionId, ct);
        if (revision == null || revision.BuildingId != buildingId)
            return AuthResult<Guid>.Fail("NOT_FOUND", "Revision not found for this building.", 404);

        if (organizationId != Guid.Empty && revision.Building.OrganizationId != organizationId)
            return AuthResult<Guid>.Fail("NOT_FOUND", "Revision not found or access denied.", 404);

        var scenario = await db.Scenarios.FirstOrDefaultAsync(s => s.Id == scenarioId && s.BuildingId == buildingId, ct);
        if (scenario == null)
            return AuthResult<Guid>.Fail("NOT_FOUND", "Scenario not found.", 404);

        if (request.ScenarioVersionId.HasValue)
        {
            var version = await db.ScenarioVersions.FirstOrDefaultAsync(v => v.Id == request.ScenarioVersionId.Value && v.ScenarioId == scenarioId, ct);
            if (version == null) return AuthResult<Guid>.Fail("NOT_FOUND", "Version not found for this scenario.", 404);
        }
        else if (request.ScenarioDraftId.HasValue)
        {
            var draft = await db.ScenarioDrafts.FirstOrDefaultAsync(d => d.Id == request.ScenarioDraftId.Value && d.ScenarioId == scenarioId, ct);
            if (draft == null) return AuthResult<Guid>.Fail("NOT_FOUND", "Draft not found for this scenario.", 404);
        }

        // L?y ServiceEntitlementId th?c t? t? DB
        var entitlementId = Guid.Empty;
        try {
            var entId = await db.Database.SqlQueryRaw<Guid>("SELECT id FROM service_entitlements WHERE organization_id = {0} AND is_active = true LIMIT 1", revision.Building.OrganizationId).FirstOrDefaultAsync(ct);
            if (entId != Guid.Empty) entitlementId = entId;
        } catch { /* B? qua n?u b?ng không t?n t?i */ }

        var sessionId = Guid.NewGuid();
        var session = new Fire3D.Domain.Entities.PlaytestSession
        {
            Id = sessionId,
            OrganizationId = revision.Building.OrganizationId,
            BuildingId = buildingId,
            RevisionId = request.RevisionId,
            ScenarioDraftId = request.ScenarioDraftId,
            ScenarioVersionId = request.ScenarioVersionId ?? Guid.Empty,
            ServiceEntitlementId = entitlementId,
            CreatedBy = actorId,
            PackageHash = request.PackageHash,
            ProtocolVersion = request.ProtocolVersion,
            ManifestSchemaVersion = request.ManifestSchemaVersion,
            RuntimeVersion = request.RuntimeVersion,
            PrepareIdempotencyKey = Guid.NewGuid().ToString(),
            Status = "Created",
            CreatedAt = DateTime.UtcNow
        };

        db.PlaytestSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return AuthResult<Guid>.Ok(sessionId);
    }

    public async Task<AuthResult<bool>> StartPlaytestAsync(Guid actorId, Guid playtestId, Guid organizationId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var session = await db.PlaytestSessions.FirstOrDefaultAsync(s => s.Id == playtestId, ct);
        
        if (session == null || (organizationId != Guid.Empty && session.OrganizationId != organizationId))
            return AuthResult<bool>.Fail("NOT_FOUND", "Playtest session not found or access denied.", 404);

        if (session.Status != "Created")
            return AuthResult<bool>.Fail("INVALID_STATE", "Playtest session must be in Created state to start.", 400);

        session.Status = "Running";
        session.StartIdempotencyKey = Guid.NewGuid().ToString();
        session.StartedAt = DateTime.UtcNow;

        // Write Audit for launch grant
        var audit = new Fire3D.Domain.Entities.AuditLog
        {
            Id = Guid.NewGuid(),
            UserId = actorId,
            OrganizationId = session.OrganizationId,
            ActorType = "User",
            Action = Fire3D.Domain.Enums.AuditAction.Create,
            TargetEntity = "PlaytestLaunchGrant",
            TargetId = playtestId,
            CorrelationId = Guid.NewGuid(),
            NewValues = """{"status": "Running"}""",
            CreatedAt = DateTime.UtcNow
        };
        db.AuditLogs.Add(audit);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return AuthResult<bool>.Ok(true);
    }
}
