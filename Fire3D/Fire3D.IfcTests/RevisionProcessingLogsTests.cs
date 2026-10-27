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
    // One entry per worker attempt: step is the job kind, status the attempt status, message its error.
    [IfcPostgresFact]
    public async Task Processing_logs_SQL_lists_worker_attempts_scoped_and_paged()
    {
        await using var db = database.Context();
        var store = new IfcReadStore(db);

        var result = await store.ListRevisionProcessingLogsAsync(database.Revision, database.Tenant, 1, 20, default);

        Assert.NotNull(result);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(new[] { database.Attempt, database.PreviousAttempt }.Order(), result.Items.Select(x => x.Id).Order());
        var failed = result.Items.Single(x => x.Id == database.PreviousAttempt);
        Assert.Equal((database.Revision, database.Job, "Geometry", "Failed", "IFC parse failed.", 0), (failed.RevisionId, failed.JobId, failed.Step, failed.Status, failed.Message, failed.AttemptNumber));
        Assert.InRange(failed.DurationMs!.Value, 1400, 1600);
        Assert.Null(await store.ListRevisionProcessingLogsAsync(database.Revision, database.OtherTenant, 1, 20, default));
        Assert.Single((await store.ListRevisionProcessingLogsAsync(database.Revision, database.Tenant, 2, 1, default))!.Items);
        Assert.Empty((await store.ListRevisionProcessingLogsAsync(database.Revision, database.Tenant, 3, 1, default))!.Items);
    }
}
