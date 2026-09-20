using Fire3D.Application.Ifc;
using Fire3D.Domain.Enums;
using Xunit;
namespace Fire3D.IfcTests;
public class GetProcessingJobTests
{
    [Theory]
    [InlineData(UserRole.OrganizationUser)] [InlineData(UserRole.PlatformAdmin)]
    public async Task Detail_preserves_database_scope(UserRole role)
    {
        var actor=RevisionAccessTests.Actor(role);
        var id=Guid.NewGuid();
        var expected=new ProcessingJobDetailResponse(new(id,Guid.NewGuid(),Guid.NewGuid(),null,"Geometry","Queued",DateTime.UtcNow),new string('a',64),null,null);
        var store=StubProxy.For<IIfcReadStore>((method,args)=>{
            Assert.Equal(nameof(IIfcReadStore.GetJobAsync),method); Assert.Equal(id,args[0]);
            Assert.Equal(role==UserRole.PlatformAdmin?null:actor.OrganizationId,args[1]);
            return Task.FromResult<ProcessingJobDetailResponse?>(expected);
        });
        var result=await new GetProcessingJobHandler(RevisionAccessTests.Accounts(actor),store).Handle(new(actor.Id,id),default);
        Assert.Same(expected,result.Value);
    }
    [Fact]
    public async Task Unknown_or_foreign_job_is_not_found()
    {
        var actor=RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var store=StubProxy.For<IIfcReadStore>((_,_)=>Task.FromResult<ProcessingJobDetailResponse?>(null));
        var result=await new GetProcessingJobHandler(RevisionAccessTests.Accounts(actor),store).Handle(new(actor.Id,Guid.NewGuid()),default);
        Assert.Equal(404,result.Error?.Status);
    }
    [Fact]
    public async Task Trainee_cannot_read_worker_metadata()
    {
        var actor=RevisionAccessTests.Actor(UserRole.Trainee);
        var store=StubProxy.For<IIfcReadStore>((_,_)=>throw new Exception("Unauthorized access"));
        var result=await new GetProcessingJobHandler(RevisionAccessTests.Accounts(actor),store).Handle(new(actor.Id,Guid.NewGuid()),default);
        Assert.Equal(403,result.Error?.Status);
    }
}
