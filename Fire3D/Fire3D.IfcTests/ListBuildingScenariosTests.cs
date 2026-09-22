using Fire3D.Application.Administration;
using Fire3D.Application.Scenarios;
using Fire3D.Application.Scenarios.Queries.ListBuildingScenarios;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class ListBuildingScenariosTests
{
    [Theory]
    [InlineData(UserRole.Trainee, 403)]
    [InlineData(UserRole.OrganizationUser, 400)]
    public async Task Invalid_or_unauthorized_requests_do_not_read_store(UserRole role, int expectedStatus)
    {
        var actor = RevisionAccessTests.Actor(role);
        var store = StubProxy.For<IScenarioReadStore>((_, _) => throw new Exception("Unexpected store access"));
        var buildingId = role == UserRole.Trainee ? Guid.NewGuid() : Guid.Empty;

        var result = await new ListBuildingScenariosQueryHandler(RevisionAccessTests.Accounts(actor), store)
            .Handle(new(actor.Id, buildingId), default);

        Assert.Equal(expectedStatus, result.Error?.Status);
    }

    [Theory]
    [InlineData(UserRole.OrganizationUser)]
    [InlineData(UserRole.PlatformAdmin)]
    public async Task List_uses_database_organization_scope(UserRole role)
    {
        var actor = RevisionAccessTests.Actor(role);
        var buildingId = Guid.NewGuid();
        var page = new PageResponse<ScenarioSummaryResponse>([], 0, 1, 20);
        var store = StubProxy.For<IScenarioReadStore>((method, args) =>
        {
            Assert.Equal(nameof(IScenarioReadStore.ListBuildingScenariosAsync), method);
            Assert.Equal(buildingId, args[0]);
            Assert.Equal(role == UserRole.PlatformAdmin ? null : actor.OrganizationId, args[1]);
            return Task.FromResult<PageResponse<ScenarioSummaryResponse>?>(page);
        });

        var result = await new ListBuildingScenariosQueryHandler(RevisionAccessTests.Accounts(actor), store)
            .Handle(new(actor.Id, buildingId), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(page, result.Value);
    }

    [Fact]
    public async Task Missing_or_foreign_building_is_not_found()
    {
        var actor = RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var store = StubProxy.For<IScenarioReadStore>((_, _) => Task.FromResult<PageResponse<ScenarioSummaryResponse>?>(null));
        var result = await new ListBuildingScenariosQueryHandler(RevisionAccessTests.Accounts(actor), store)
            .Handle(new(actor.Id, Guid.NewGuid()), default);
        Assert.Equal(404, result.Error?.Status);
    }
}
