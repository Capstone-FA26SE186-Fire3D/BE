using Fire3D.Application.Ifc;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Ifc;
using Xunit;
namespace Fire3D.IfcTests;
public class ValidationRunTests
{
    [Fact]
    public async Task Trainee_cannot_read_validation()
    {
        var actor=RevisionAccessTests.Actor(UserRole.Trainee);
        var store=StubProxy.For<IIfcReadStore>((_,_)=>throw new Exception("Unexpected access"));
        var result=await new GetValidationRunHandler(RevisionAccessTests.Accounts(actor),store).Handle(new(actor.Id,Guid.NewGuid()),default);
        Assert.Equal(403,result.Error?.Status);
    }
    [Fact]
    public async Task Scope_is_database_owned_and_missing_run_is_404()
    {
        var actor=RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var store=StubProxy.For<IIfcReadStore>((method,args)=>{
            Assert.Equal(nameof(IIfcReadStore.GetValidationAsync),method); Assert.Equal(actor.OrganizationId,args[1]);
            return Task.FromResult<ValidationRunResponse?>(null);
        });
        var result=await new GetValidationRunHandler(RevisionAccessTests.Accounts(actor),store).Handle(new(actor.Id,Guid.NewGuid()),default);
        Assert.Equal(404,result.Error?.Status);
    }
}
public sealed partial class IfcReadSqlTests
{
    [IfcPostgresFact]
    public async Task Validation_has_attempt_provenance_and_history_is_scoped()
    {
        await using var db=database.Context();
        var store=new IfcReadStore(db);
        var current=await store.GetValidationAsync(database.Validation,database.Tenant,default);
        Assert.NotNull(current); Assert.Equal(database.Attempt,current.ProcessingAttemptId);
        Assert.Equal("Passed",current.Status);
        Assert.Null(await store.GetValidationAsync(database.Validation,database.OtherTenant,default));
        Assert.NotNull(await store.GetValidationAsync(database.PreviousValidation,database.Tenant,default));
        Assert.NotNull(await store.GetValidationAsync(database.Validation,null,default));
    }
}
