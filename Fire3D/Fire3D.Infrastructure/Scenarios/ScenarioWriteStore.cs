using System.Text.Json.Nodes;
using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios;
using Fire3D.Application.Scenarios.Commands.CreateScenario;
using Fire3D.Application.Scenarios.Commands.CreateScenarioDraft;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fire3D.Infrastructure.Scenarios;

public sealed class ScenarioWriteStore(Fire3DDbContext db) : IScenarioWriteStore
{
    public async Task<AuthResult<Guid>> CreateScenarioAsync(Guid actorId, Guid buildingId, Guid organizationId, CreateScenarioRequest request, CancellationToken ct)
    {
        var building = await db.Buildings.FirstOrDefaultAsync(b => b.Id == buildingId && b.IsActive && b.DeletedAt == null, ct);
        if (building == null || (organizationId != Guid.Empty && building.OrganizationId != organizationId))
            return AuthResult<Guid>.Fail("NOT_FOUND", "Building not found or access denied.", 404);

        var scenario = new Fire3D.Domain.Entities.Scenario
        {
            Id = Guid.NewGuid(),
            BuildingId = buildingId,
            OrganizationId = building.OrganizationId,
            Name = request.Name,
            CreatedBy = actorId,
            CreatedAt = DateTime.UtcNow
        };

        db.Scenarios.Add(scenario);
        await db.SaveChangesAsync(ct);
        return AuthResult<Guid>.Ok(scenario.Id);
    }

    public async Task<AuthResult<Guid>> CreateScenarioDraftAsync(Guid actorId, Guid scenarioId, Guid organizationId, CreateScenarioDraftRequest request, CancellationToken ct)
    {
        var scenario = await db.Scenarios.FirstOrDefaultAsync(s => s.Id == scenarioId, ct);
        if (scenario == null || (organizationId != Guid.Empty && scenario.OrganizationId != organizationId))
            return AuthResult<Guid>.Fail("NOT_FOUND", "Scenario not found or access denied.", 404);

        var revision = await db.Revisions.FirstOrDefaultAsync(r => r.Id == request.RevisionId, ct);
        if (revision == null || revision.BuildingId != scenario.BuildingId)
            return AuthResult<Guid>.Fail("VALIDATION_ERROR", "Revision not found or does not belong to the same building.", 400);

        // Find max draft number
        var maxDraft = await db.ScenarioDrafts
            .Where(d => d.ScenarioId == scenarioId)
            .MaxAsync(d => (int?)d.DraftNumber, ct) ?? 0;

        var draft = new Fire3D.Domain.Entities.ScenarioDraft
        {
            Id = Guid.NewGuid(),
            ScenarioId = scenarioId,
            RevisionId = request.RevisionId,
            OrganizationId = scenario.OrganizationId,
            BuildingId = scenario.BuildingId,
            DraftNumber = maxDraft + 1,
            State = new JsonObject(),
            Source = "manual",
            CreatedBy = actorId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Version = 1
        };

        db.ScenarioDrafts.Add(draft);
        await db.SaveChangesAsync(ct);
        return AuthResult<Guid>.Ok(draft.Id);
    }
}
