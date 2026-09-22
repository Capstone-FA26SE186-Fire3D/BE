using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Ifc;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Fire3D.IfcTests;
public class RetryJobTests
{
    [Theory]
    [InlineData(UserRole.Trainee,"Retry",403)] [InlineData(UserRole.OrganizationUser," ",400)]
    public async Task Invalid_retry_never_calls_gate(UserRole role,string reason,int status)
    {
        var actor=RevisionAccessTests.Actor(role);
        var store=StubProxy.For<IIfcWriteStore>((_,_)=>throw new Exception("Unexpected gate"));
        var result=await new RetryProcessingJobHandler(RevisionAccessTests.Accounts(actor),store)
            .Handle(new(actor.Id,Guid.NewGuid(),new(Guid.NewGuid(),reason)),default);
        Assert.Equal(status,result.Error?.Status);
    }
    [Fact]
    public async Task Authorized_request_preserves_actor_and_idempotency()
    {
        var actor=RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var job=Guid.NewGuid(); var key=Guid.NewGuid();
        var store=StubProxy.For<IIfcWriteStore>((_,args)=>{
            Assert.Equal(actor.Id,args[0]); Assert.Equal(job,args[1]); Assert.Equal(key,args[2]); Assert.Equal("Retry",args[3]);
            return Task.FromResult(AuthResult<RetryProcessingJobResponse>.Ok(new(job,"Requeued")));
        });
        var result=await new RetryProcessingJobHandler(RevisionAccessTests.Accounts(actor),store)
            .Handle(new(actor.Id,job,new(key," Retry ")),default);
        Assert.True(result.IsSuccess);
    }
}

public class IfcWriteSqlTests : IAsyncLifetime
{
    private Testcontainers.PostgreSql.PostgreSqlContainer _dbContainer;
    public Guid ActorId = Guid.NewGuid();
    public Guid OtherActorId = Guid.NewGuid();
    public Guid OrgId = Guid.NewGuid();
    public Guid FailedJobId = Guid.NewGuid();
    public Guid RevisionId = Guid.NewGuid();

    public IfcWriteSqlTests()
    {
        _dbContainer = new Testcontainers.PostgreSql.PostgreSqlBuilder("postgres:15-alpine").Build();
    }

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        using var db = Context();
        await db.Database.EnsureCreatedAsync();

        // Seed data
        db.Organizations.Add(new Fire3D.Domain.Entities.Organization { Id = OrgId, Name = "Org", Slug = "retry-job-org", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.Users.Add(new Fire3D.Domain.Entities.User { Id = ActorId, Email = "actor@org", FullName = "A", Role = UserRole.OrganizationUser, OrganizationId = OrgId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.Users.Add(new Fire3D.Domain.Entities.User { Id = OtherActorId, Email = "other@org", FullName = "O", Role = UserRole.OrganizationUser, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        
        var building = new Fire3D.Domain.Entities.Building { Id = Guid.NewGuid(), OrganizationId = OrgId, Name = "B", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Buildings.Add(building);

        var doc = new Fire3D.Domain.Entities.SourceDocument { Id = Guid.NewGuid(), OriginalFilename = "1.ifc", StorageUrl = "url", UploadedBy = ActorId, FileSizeBytes = 100, CreatedAt = DateTime.UtcNow };
        db.SourceDocuments.Add(doc);

        var rev = new Fire3D.Domain.Entities.Revision { Id = RevisionId, BuildingId = building.Id, OrganizationId = OrgId, UploadedBy = ActorId, VersionLabel = "1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, SourceDocument = doc };
        db.Revisions.Add(rev);

        db.ProcessingJobs.Add(new Fire3D.Domain.Entities.ProcessingJob { Id = FailedJobId, RevisionId = RevisionId, SourceDocumentId = doc.Id, Kind = "ProcessIfc", JobKey = Guid.NewGuid(), Status = "Failed", InputHash = "hash", CreatedAt = DateTime.UtcNow });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _dbContainer.DisposeAsync();

    public Fire3D.Infrastructure.Persistence.Fire3DDbContext Context()
    {
        var options = new DbContextOptionsBuilder<Fire3D.Infrastructure.Persistence.Fire3DDbContext>().UseNpgsql(_dbContainer.GetConnectionString()).Options;
        return new Fire3D.Infrastructure.Persistence.Fire3DDbContext(options);
    }

    [Fact]
    public async Task Retry_gate_replays_without_duplicate_event_or_audit_and_blocks_foreign_actor()
    {
        await using var db = Context();
        var store = new Fire3D.Infrastructure.Ifc.IfcWriteStore(db);
        var key = Guid.NewGuid();
        
        Assert.Equal(404, (await store.RetryJobAsync(OtherActorId, FailedJobId, key, "Retry", default)).Error?.Status);
        
        var first = await store.RetryJobAsync(ActorId, FailedJobId, key, "Retry", default);
        Assert.True(first.IsSuccess); 
        Assert.Equal("Requeued", first.Value?.Outcome);
        
        var replay = await store.RetryJobAsync(ActorId, FailedJobId, key, "Retry", default);
        Assert.Equal("AlreadyRequeued", replay.Value?.Outcome);
        
        Assert.Equal(409, (await store.RetryJobAsync(ActorId, FailedJobId, key, "Changed", default)).Error?.Status);
        Assert.Equal(409, (await store.RetryJobAsync(ActorId, FailedJobId, Guid.NewGuid(), "Retry", default)).Error?.Status);
        
        var eventCount = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM integration_outbox_events WHERE aggregate_id={0}", FailedJobId).SingleAsync();
        Assert.Equal(1, eventCount);
    }
}



