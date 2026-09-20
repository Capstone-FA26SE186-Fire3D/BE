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
#if false
public sealed partial class IfcReadSqlTests
{
    [IfcPostgresFact]
    public async Task Retry_gate_replays_without_duplicate_event_or_audit_and_blocks_foreign_actor()
    {
        await using var db=database.Context();
        var store=new IfcWriteStore(db);
        var key=Guid.NewGuid();
        Assert.Equal(404,(await store.RetryJobAsync(database.OtherActor,database.FailedJob,key,"Retry",default)).Error?.Status);
        var first=await store.RetryJobAsync(database.Actor,database.FailedJob,key,"Retry",default);
        Assert.True(first.IsSuccess); Assert.Equal("Requeued",first.Value?.Outcome);
        var replay=await store.RetryJobAsync(database.Actor,database.FailedJob,key,"Retry",default);
        Assert.Equal("AlreadyRequeued",replay.Value?.Outcome);
        Assert.Equal(409,(await store.RetryJobAsync(database.Actor,database.FailedJob,key,"Changed",default)).Error?.Status);
        Assert.Equal(409,(await store.RetryJobAsync(database.Actor,database.FailedJob,Guid.NewGuid(),"Retry",default)).Error?.Status);
        var eventCount=await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM integration_outbox_events WHERE aggregate_id={database.FailedJob}").SingleAsync();
        var auditCount=await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM audit_logs WHERE target_id={database.FailedJob}").SingleAsync();
        Assert.Equal(1,eventCount); Assert.Equal(1,auditCount);
    }
}
#endif
