using Fire3D.Application.Ifc;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Ifc;
using Xunit;
namespace Fire3D.IfcTests;
public class JobQaTests
{
    [Theory]
    [InlineData(UserRole.Trainee,1,403)] [InlineData(UserRole.OrganizationUser,0,400)]
    public async Task Invalid_qa_requests_do_not_query_store(UserRole role,int page,int expected)
    {
        var actor=RevisionAccessTests.Actor(role);
        var store=StubProxy.For<IIfcReadStore>((_,_)=>throw new Exception("Unexpected access"));
        var result=await new GetJobQaHandler(RevisionAccessTests.Accounts(actor),store).Handle(new(actor.Id,Guid.NewGuid(),page),default);
        Assert.Equal(expected,result.Error?.Status);
    }
    [Fact]
    public async Task Foreign_job_returns_not_found()
    {
        var actor=RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var store=StubProxy.For<IIfcReadStore>((_,args)=>{
            Assert.Equal(actor.OrganizationId,args[1]); return Task.FromResult<JobQaResponse?>(null);
        });
        var result=await new GetJobQaHandler(RevisionAccessTests.Accounts(actor),store).Handle(new(actor.Id,Guid.NewGuid()),default);
        Assert.Equal(404,result.Error?.Status);
    }
}
public sealed partial class IfcReadSqlTests
{
    [IfcPostgresFact]
    public async Task Job_QA_only_uses_current_attempt_not_failed_history()
    {
        await using var db=database.Context();
        var store=new IfcReadStore(db);
        var qa=await store.GetJobQaAsync(database.Job,database.Tenant,1,20,default);
        Assert.NotNull(qa); Assert.Equal(database.Attempt,qa.CurrentAttemptId);
        Assert.Equal(database.Validation,Assert.Single(qa.ValidationRuns.Items).Id);
        Assert.Equal(1,qa.ValidationRuns.TotalCount);
        Assert.Null(await store.GetJobQaAsync(database.Job,database.OtherTenant,1,20,default));
        var next=await store.GetJobQaAsync(database.Job,database.Tenant,2,1,default);
        Assert.NotNull(next); Assert.Empty(next.ValidationRuns.Items);
    }
}
