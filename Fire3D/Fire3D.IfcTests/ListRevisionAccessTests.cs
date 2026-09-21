using Fire3D.Application.Administration;
using Fire3D.Application.Buildings;
using Fire3D.Application.Buildings.Queries.ListRevisions;
using Fire3D.Domain.Enums;
using Xunit;
namespace Fire3D.IfcTests;
public class ListRevisionAccessTests
{
    [Theory]
    [InlineData(0,20)] [InlineData(1,0)] [InlineData(1,101)] [InlineData(100001,20)]
    public async Task Invalid_pagination_never_reads_store(int page,int size)
    {
        var actor=RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var store=StubProxy.For<IBuildingStore>((_,_)=>throw new Exception("Unexpected access"));
        var result=await new ListRevisionsQueryHandler(store,RevisionAccessTests.Accounts(actor)).Handle(new(actor.Id,Guid.NewGuid(),page,size),default);
        Assert.Equal(400,result.Error?.Status);
    }
    [Fact]
    public async Task Trainee_is_forbidden()
    {
        var actor=RevisionAccessTests.Actor(UserRole.Trainee);
        var store=StubProxy.For<IBuildingStore>((_,_)=>throw new Exception("Unexpected access"));
        var result=await new ListRevisionsQueryHandler(store,RevisionAccessTests.Accounts(actor)).Handle(new(actor.Id,Guid.NewGuid()),default);
        Assert.Equal(403,result.Error?.Status);
    }
    [Fact]
    public async Task Missing_or_foreign_building_is_not_an_empty_success()
    {
        var actor=RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var store=StubProxy.For<IBuildingStore>((method,args)=>{
            Assert.Equal(nameof(IBuildingStore.RevisionBuildingExistsAsync),method);
            Assert.Equal(actor.OrganizationId,args[1]); return Task.FromResult(false);
        });
        var result=await new ListRevisionsQueryHandler(store,RevisionAccessTests.Accounts(actor)).Handle(new(actor.Id,Guid.NewGuid()),default);
        Assert.Equal(404,result.Error?.Status);
    }
    [Theory]
    [InlineData(UserRole.OrganizationUser)] [InlineData(UserRole.PlatformAdmin)]
    public async Task Scope_and_pagination_are_preserved(UserRole role)
    {
        var actor=RevisionAccessTests.Actor(role);
        var building=Guid.NewGuid();
        var page=new PageResponse<RevisionResponse>([],0,2,10);
        var store=StubProxy.For<IBuildingStore>((method,args)=>{
            Assert.Equal(building,args[0]);
            Assert.Equal(role==UserRole.PlatformAdmin?null:actor.OrganizationId,args[1]);
            if(method==nameof(IBuildingStore.RevisionBuildingExistsAsync)) return Task.FromResult(true);
            Assert.Equal(nameof(IBuildingStore.ListRevisionsAsync),method);
            Assert.Equal(2,args[2]); Assert.Equal(10,args[3]); return Task.FromResult(page);
        });
        var result=await new ListRevisionsQueryHandler(store,RevisionAccessTests.Accounts(actor)).Handle(new(actor.Id,building,2,10),default);
        Assert.True(result.IsSuccess); Assert.Same(page,result.Value);
    }
}
