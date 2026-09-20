using Fire3D.Application.Ifc;
using Fire3D.Application.Administration;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Ifc;
using Xunit;
namespace Fire3D.IfcTests;
public class RevisionArtifactsTests
{
    [Theory]
    [InlineData(UserRole.Trainee,1,20,403)]
    [InlineData(UserRole.OrganizationUser,0,20,400)]
    [InlineData(UserRole.OrganizationUser,1,101,400)]
    public async Task Invalid_requests_do_not_reach_storage(UserRole role,int page,int size,int expected)
    {
        var actor=RevisionAccessTests.Actor(role);
        var store=StubProxy.For<IIfcReadStore>((_,_)=>throw new Exception("Unexpected access"));
        var result=await new ListRevisionArtifactsHandler(RevisionAccessTests.Accounts(actor),store).Handle(new(actor.Id,Guid.NewGuid(),page,size),default);
        Assert.Equal(expected,result.Error?.Status);
    }
    [Fact]
    public async Task Tenant_scope_is_preserved_for_missing_resources()
    {
        var actor=RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var store=StubProxy.For<IIfcReadStore>((method,args)=>{
            Assert.Equal(nameof(IIfcReadStore.ListRevisionArtifactsAsync),method); Assert.Equal(actor.OrganizationId,args[1]);
            return Task.FromResult<PageResponse<RevisionArtifactResponse>?>(null);
        });
        var result=await new ListRevisionArtifactsHandler(RevisionAccessTests.Accounts(actor),store).Handle(new(actor.Id,Guid.NewGuid()),default);
        Assert.Equal(404,result.Error?.Status);
    }
}
public sealed partial class IfcReadSqlTests
{
    [IfcPostgresFact]
    public async Task RevisionArtifacts_SQL_is_scoped_paged_and_preserves_provenance()
    {
        await using var db=database.Context();
        var store=new IfcReadStore(db);
        var result=await store.ListRevisionArtifactsAsync(database.Revision,database.Tenant,1,20,default);
        Assert.NotNull(result); var item=Assert.Single(result.Items);
        Assert.Equal(database.Artifact,item.Id);
        Assert.Equal(database.Attempt,item.AttemptId); Assert.Equal(new string('b',64),item.Sha256Hash); Assert.True(item.IsCurrentAttempt);
        Assert.Null(await store.ListRevisionArtifactsAsync(database.Revision,database.OtherTenant,1,20,default));
        Assert.NotNull(await store.ListRevisionArtifactsAsync(database.Revision,null,1,20,default));
        var next=await store.ListRevisionArtifactsAsync(database.Revision,database.Tenant,2,1,default);
        Assert.NotNull(next); Assert.Empty(next.Items); Assert.Equal(1,next.TotalCount);
    }
}
