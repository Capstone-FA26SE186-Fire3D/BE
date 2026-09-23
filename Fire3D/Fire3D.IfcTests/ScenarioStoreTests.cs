using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Fire3D.Application.Scenarios.Commands.CreateScenario;
using Fire3D.Application.Scenarios.Commands.CreateScenarioDraft;
using Fire3D.Application.Scenarios.Dto;
using Fire3D.Domain.Entities;
using Fire3D.Infrastructure.Persistence;
using Fire3D.Infrastructure.Scenarios;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Fire3D.IfcTests;

public class ScenarioStoreTests : IAsyncLifetime
{
        private PostgreSqlContainer _dbContainer;
    public ScenarioStoreTests()
    {
        _dbContainer = new PostgreSqlBuilder("postgres:15-alpine").Build();
    }
    public async Task InitializeAsync() => await _dbContainer.StartAsync();
    public async Task DisposeAsync() => await _dbContainer.DisposeAsync();

    private Fire3DDbContext GetDbContext()
    {
        var options = IfcTestOptions.Create(_dbContainer.GetConnectionString());
        var db = new Fire3DDbContext(options);
        db.Database.EnsureCreated();
        // Seed prerequisites
        var orgId = Guid.NewGuid();
        var buildingId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        db.Organizations.Add(new Organization { Id = orgId, IsActive = true, Name = "Org", Slug = "scenario-store-org" });
        db.Users.Add(new User { Id = userId, OrganizationId = orgId, Email = "scenario@example.test", FullName = "Test editor", Role = Fire3D.Domain.Enums.UserRole.OrganizationUser, IsActive = true });
        db.Buildings.Add(new Building { Id = buildingId, OrganizationId = orgId, Name = "Building", IsActive = true, CreatedBy = userId });
        db.Revisions.Add(new Revision { Id = revisionId, BuildingId = buildingId, OrganizationId = orgId, UploadedBy = userId, VersionLabel = "v1" });
        db.SaveChanges();

        return db;
    }

    [Fact]
    public async Task ScenarioFlow_Create_Draft_Update_Snapshot_ShouldSucceed()
    {
        // Arrange
        using var db = GetDbContext();
        var store = new ScenarioWriteStore(db);
        
        var org = await db.Organizations.FirstAsync();
        var building = await db.Buildings.FirstAsync();
        var revision = await db.Revisions.FirstAsync();
        var actorId = (await db.Users.SingleAsync()).Id;

        // 1. Create Scenario (D09)
        var createReq = new CreateScenarioRequest(building.Id, "Fire Evacuation Scenario");
        var scenarioResult = await store.CreateScenarioAsync(actorId, building.Id, org.Id, createReq, CancellationToken.None);
        
        Assert.True(scenarioResult.IsSuccess);
        var scenarioId = scenarioResult.Value;
        Assert.NotEqual(Guid.Empty, scenarioId);

        // 2. Create Draft (D10)
        var draftReq = new CreateScenarioDraftRequest(revision.Id);
        var draftResult = await store.CreateScenarioDraftAsync(actorId, scenarioId, org.Id, draftReq, CancellationToken.None);
        
        Assert.True(draftResult.IsSuccess);
        var draftId = draftResult.Value;

        // Verify Draft creation
        var draft = await db.ScenarioDrafts.FindAsync(draftId);
        Assert.NotNull(draft);
        Assert.Equal(1, draft.DraftNumber);
        Assert.NotEqual(0u, draft.Version); // PostgreSQL xmin is a transaction ID, not a counter starting at 1.
        var originalVersion = draft.Version;
        Assert.NotNull(draft.State);

        // 3. Update Draft (D11)
        var updateReq = new ScenarioDraftStateDto(
            SpawnPoints: new System.Collections.Generic.List<SpawnPoint> { new SpawnPoint(1, 2, 3, 90) },
            Hazards: new System.Collections.Generic.List<Hazard>(),
            ScoringConfig: new ScoringConfig(100, 300, 10),
            RoutingConfig: new RoutingConfig(new System.Collections.Generic.List<string>())
        );

        var updateResult = await store.UpdateScenarioDraftAsync(actorId, draftId, originalVersion, updateReq, org.Id, CancellationToken.None);
        Assert.True(updateResult.IsSuccess);

        // 4. Update Concurrency Conflict
        var conflictResult = await store.UpdateScenarioDraftAsync(actorId, draftId, originalVersion, updateReq, org.Id, CancellationToken.None);
        Assert.False(conflictResult.IsSuccess);
        Assert.Equal(409, conflictResult.Error!.Status);

        // 5. Snapshot Draft (D12)
        var snapshotResult = await store.SnapshotScenarioDraftAsync(actorId, draftId, org.Id, CancellationToken.None);
        Assert.True(snapshotResult.IsSuccess);
        var snapshotId = snapshotResult.Value;

        var snapshot = await db.ScenarioVersions.FindAsync(snapshotId);
        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot.VersionNumber);
        Assert.Equal("Snapshot 1", snapshot.Name);
        Assert.Equal(300, snapshot.TimeLimitSeconds); // Extracted from JSON config!
    }
}


