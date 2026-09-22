using Fire3D.Application.Administration;
using Fire3D.Application.Ifc;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Ifc;
using Xunit;

namespace Fire3D.IfcTests;

public class RevisionProcessingLogsTests
{
    [Theory]
    [InlineData(UserRole.Trainee, 1, 20, 403)]
    [InlineData(UserRole.OrganizationUser, 0, 20, 400)]
    [InlineData(UserRole.OrganizationUser, 1, 101, 400)]
    public async Task Invalid_requests_do_not_reach_storage(UserRole role, int page, int pageSize, int expectedStatus)
    {
        var actor = RevisionAccessTests.Actor(role);
        var store = StubProxy.For<IIfcReadStore>((_, _) => throw new Exception("Unexpected access"));

        var result = await new ListRevisionProcessingLogsHandler(RevisionAccessTests.Accounts(actor), store)
            .Handle(new(actor.Id, Guid.NewGuid(), page, pageSize), default);

        Assert.Equal(expectedStatus, result.Error?.Status);
    }

    [Fact]
    public async Task Missing_or_foreign_revision_is_not_found_with_tenant_scope()
    {
        var actor = RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var store = StubProxy.For<IIfcReadStore>((method, args) =>
        {
            Assert.Equal(nameof(IIfcReadStore.ListRevisionProcessingLogsAsync), method);
            Assert.Equal(actor.OrganizationId, args[1]);
            return Task.FromResult<PageResponse<RevisionProcessingLogResponse>?>(null);
        });

        var result = await new ListRevisionProcessingLogsHandler(RevisionAccessTests.Accounts(actor), store)
            .Handle(new(actor.Id, Guid.NewGuid()), default);

        Assert.Equal(404, result.Error?.Status);
    }
}

public sealed partial class IfcReadSqlTests
{
    [IfcPostgresFact]
    public async Task Processing_logs_SQL_is_scoped_paged_and_sanitized()
    {
        await using var db = database.Context();
        var store = new IfcReadStore(db);

        var result = await store.ListRevisionProcessingLogsAsync(database.Revision, database.Tenant, 1, 20, default);

        Assert.NotNull(result);
        var item = Assert.Single(result.Items);
        Assert.Equal(database.Log, item.Id);
        Assert.Equal("Parse", item.Step);
        Assert.Equal("Success", item.Status);
        Assert.Equal("IFC parsed.", item.Message);
        Assert.Null(await store.ListRevisionProcessingLogsAsync(database.Revision, database.OtherTenant, 1, 20, default));
        Assert.Empty((await store.ListRevisionProcessingLogsAsync(database.Revision, database.Tenant, 2, 1, default))!.Items);
    }
}
