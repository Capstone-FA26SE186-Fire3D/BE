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

    public async Task<AuthResult<uint>> UpdateScenarioDraftAsync(Guid actorId, Guid draftId, uint expectedVersion, Fire3D.Application.Scenarios.Dto.ScenarioDraftStateDto state, Guid organizationId, CancellationToken ct)
    {
        var draft = await db.ScenarioDrafts.FirstOrDefaultAsync(d => d.Id == draftId, ct);
        if (draft == null || (organizationId != Guid.Empty && draft.OrganizationId != organizationId))
            return AuthResult<uint>.Fail("NOT_FOUND", "Scenario draft not found or access denied.", 404);

        if (draft.Version != expectedVersion)
            return AuthResult<uint>.Fail("CONFLICT", "The draft has been modified by someone else. Please reload.", 409);

        // Serialize state to JsonNode
        var stateJson = System.Text.Json.JsonSerializer.Serialize(state);
        draft.State = System.Text.Json.Nodes.JsonNode.Parse(stateJson)!;
        draft.UpdatedAt = DateTime.UtcNow;
        // CreatedBy shouldn't be overwritten, but we could track LastUpdatedBy if needed

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return AuthResult<uint>.Fail("CONFLICT", "The draft was modified concurrently. Please reload.", 409);
        }

        // Fetch the updated version (xmin changes on update in postgres, EF updates it automatically)
        return AuthResult<uint>.Ok(draft.Version);
    }

    public async Task<AuthResult<Guid>> SnapshotScenarioDraftAsync(Guid actorId, Guid draftId, Guid organizationId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var draft = await db.ScenarioDrafts.FirstOrDefaultAsync(d => d.Id == draftId, ct);
        if (draft == null || (organizationId != Guid.Empty && draft.OrganizationId != organizationId))
            return AuthResult<Guid>.Fail("NOT_FOUND", "Scenario draft not found or access denied.", 404);

        // Calculate a simple SHA256 hash of the JSON state for the snapshot
        var stateJson = draft.State.ToJsonString();
        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stateJson));
        var stateHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        // Check max version number
        var maxVersion = await db.ScenarioVersions
            .Where(v => v.ScenarioId == draft.ScenarioId)
            .MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0;

        var snapshotId = Guid.NewGuid();
        var snapshot = new Fire3D.Domain.Entities.ScenarioVersion
        {
            Id = snapshotId,
            ScenarioId = draft.ScenarioId,
            RevisionId = draft.RevisionId,
            OrganizationId = draft.OrganizationId,
            BuildingId = draft.BuildingId,
            VersionNumber = maxVersion + 1,
            Name = $"Snapshot {maxVersion + 1}",
            SchemaVersion = "v1",
            AlgorithmVersion = "v1",
            RandomSeed = 0,
            TimeLimitSeconds = draft.State["scoringConfig"]?["timeLimitSeconds"]?.GetValue<int>() ?? 0,
            SpawnConfig = draft.State["spawnPoints"]?.ToJsonString() ?? "[]",
            GoalConfig = draft.State["goals"]?.ToJsonString() ?? "[]",
            FireSourceConfig = draft.State["hazards"]?.ToJsonString() ?? "[]",
            NpcConfig = draft.State["npcs"]?.ToJsonString() ?? "[]",
            BlockedElements = draft.State["blockedElements"]?.ToJsonString() ?? "[]",
            RoutingConfig = draft.State["routingConfig"]?.ToJsonString() ?? "{}",
            ScoringConfig = draft.State["scoringConfig"]?.ToJsonString() ?? "{}",
            ModePolicy = draft.State["modePolicy"]?.ToJsonString() ?? "{}",
            SafetyThresholds = draft.State["safetyThresholds"]?.ToJsonString() ?? "{}",
            ReplanIntervalSeconds = draft.State["replanIntervalSeconds"]?.GetValue<int>() ?? 0,
            ScenarioHash = stateHash,
            CreatedBy = actorId,
            CreatedAt = DateTime.UtcNow
        };

        db.ScenarioVersions.Add(snapshot);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return AuthResult<Guid>.Ok(snapshotId);
    }
}
