using Fire3D.Application.Scenarios.Commands.PreparePlaytestSession;
using Fire3D.Domain.Entities;
using Fire3D.Infrastructure.Persistence;
using Fire3D.Infrastructure.Scenarios;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class PlaytestEntitlementContainmentTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:15-alpine").Build();
    private readonly Guid organizationId = Guid.NewGuid();
    private readonly Guid ownerId = Guid.NewGuid();
    private readonly Guid otherOrganizationUserId = Guid.NewGuid();
    private readonly Guid buildingId = Guid.NewGuid();
    private readonly Guid revisionId = Guid.NewGuid();
    private readonly Guid scenarioId = Guid.NewGuid();
    private readonly Guid versionId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        db.Organizations.Add(new Organization { Id = organizationId, Name = "Org", Slug = "playtest-containment-org", IsActive = true, CreatedAt = now, UpdatedAt = now });
        db.Users.AddRange(
            new User { Id = ownerId, Email = "owner@example.test", Role = Fire3D.Domain.Enums.UserRole.OrganizationUser, OrganizationId = organizationId, IsActive = true, CreatedAt = now, UpdatedAt = now },
            new User { Id = otherOrganizationUserId, Email = "other@example.test", Role = Fire3D.Domain.Enums.UserRole.OrganizationUser, OrganizationId = organizationId, IsActive = true, CreatedAt = now, UpdatedAt = now });
        db.Buildings.Add(new Building { Id = buildingId, OrganizationId = organizationId, Name = "Building", IsActive = true, CreatedBy = ownerId, CreatedAt = now, UpdatedAt = now });
        db.Revisions.Add(new Revision { Id = revisionId, BuildingId = buildingId, OrganizationId = organizationId, UploadedBy = ownerId, VersionLabel = "1", CreatedAt = now, UpdatedAt = now });
        db.Scenarios.Add(new Scenario { Id = scenarioId, BuildingId = buildingId, OrganizationId = organizationId, Name = "Scenario", CreatedBy = ownerId, CreatedAt = now });
        db.ScenarioVersions.Add(new ScenarioVersion
        {
            Id = versionId, ScenarioId = scenarioId, RevisionId = revisionId, BuildingId = buildingId, OrganizationId = organizationId,
            VersionNumber = 1, Name = "Scenario v1", SchemaVersion = "1", AlgorithmVersion = "1", TimeLimitSeconds = 60,
            SpawnConfig = "{}", GoalConfig = "{}", FireSourceConfig = "{}", NpcConfig = "{}", BlockedElements = "[]",
            RoutingConfig = "{}", ScoringConfig = "{}", ModePolicy = "{}", SafetyThresholds = "{}", ReplanIntervalSeconds = 5,
            ScenarioHash = new string('a', 64), CreatedBy = ownerId, CreatedAt = now
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task Prepare_fails_closed_when_entitlement_contract_is_unavailable()
    {
        await using var db = Context();

        var result = await new FailClosedPlaytestWriteStore(db).PreparePlaytestAsync(
            ownerId, buildingId, scenarioId, organizationId,
            new(revisionId, null, versionId, "package-hash", "1.0", "1.0", "1.0.0"), default);

        Assert.Equal(503, result.Error?.Status);
        Assert.Equal("ENTITLEMENT_UNAVAILABLE", result.Error?.Code);
        Assert.Empty(await db.PlaytestSessions.ToListAsync());
    }

    [Fact]
    public async Task Start_does_not_allow_another_organization_user_to_launch_owner_playtest()
    {
        await using var db = Context();
        var playtestId = Guid.NewGuid();
        db.PlaytestSessions.Add(new PlaytestSession
        {
            Id = playtestId, OrganizationId = organizationId, BuildingId = buildingId, RevisionId = revisionId,
            ScenarioVersionId = versionId, ServiceEntitlementId = Guid.NewGuid(), CreatedBy = ownerId,
            PackageHash = "package-hash", ProtocolVersion = "1.0", ManifestSchemaVersion = "1.0",
            PrepareIdempotencyKey = Guid.NewGuid().ToString(), Status = "Created", CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await new FailClosedPlaytestWriteStore(db).StartPlaytestAsync(otherOrganizationUserId, playtestId, organizationId, default);

        Assert.Equal(404, result.Error?.Status);
        Assert.Equal("Created", (await db.PlaytestSessions.SingleAsync()).Status);
    }

    private Fire3DDbContext Context() => new(IfcTestOptions.Create(container.GetConnectionString()));
}
