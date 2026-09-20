using Fire3D.Application.Ifc;
using Fire3D.Application.Administration;
using Fire3D.Domain.Enums;
using Xunit;
namespace Fire3D.IfcTests;
public class ProcessingJobAccessTests
{
    [Theory]
    [InlineData(UserRole.OrganizationUser)] [InlineData(UserRole.PlatformAdmin)]
    public async Task List_jobs_uses_database_scope(UserRole role)
    {
        var actor=RevisionAccessTests.Actor(role);
        var revision=Guid.NewGuid();
        var page=new PageResponse<ProcessingJobResponse>([],0,2,10);
        var store=StubProxy.For<IIfcReadStore>((method,args)=>{
            Assert.Equal(nameof(IIfcReadStore.ListJobsAsync),method);
            Assert.Equal(revision,args[0]);
            Assert.Equal(role==UserRole.PlatformAdmin?null:actor.OrganizationId,args[1]);
            Assert.Equal(2,args[2]); Assert.Equal(10,args[3]);
            return Task.FromResult<PageResponse<ProcessingJobResponse>?>(page);
        });
        var result=await new ListProcessingJobsHandler(RevisionAccessTests.Accounts(actor),store)
            .Handle(new(actor.Id,revision,2,10),default);
        Assert.Same(page,result.Value);
    }
    [Fact]
    public async Task Foreign_revision_is_not_found()
    {
        var actor=RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var store=StubProxy.For<IIfcReadStore>((_,_)=>Task.FromResult<PageResponse<ProcessingJobResponse>?>(null));
        var result=await new ListProcessingJobsHandler(RevisionAccessTests.Accounts(actor),store).Handle(new(actor.Id,Guid.NewGuid()),default);
        Assert.Equal(404,result.Error?.Status);
    }
    [Theory]
    [InlineData(UserRole.Trainee,1,403)] [InlineData(UserRole.OrganizationUser,0,400)]
    public async Task Invalid_requests_do_not_reach_store(UserRole role,int page,int status)
    {
        var actor=RevisionAccessTests.Actor(role);
        var store=StubProxy.For<IIfcReadStore>((_,_)=>throw new Exception("Unexpected access"));
        var result=await new ListProcessingJobsHandler(RevisionAccessTests.Accounts(actor),store).Handle(new(actor.Id,Guid.NewGuid(),page),default);
        Assert.Equal(status,result.Error?.Status);
    }
}
