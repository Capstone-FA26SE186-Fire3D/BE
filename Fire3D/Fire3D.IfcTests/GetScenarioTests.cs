using Fire3D.Application.Scenarios;
using Fire3D.Application.Scenarios.Queries.GetScenario;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class GetScenarioTests
{
    [Theory]
    [InlineData(UserRole.Trainee, 403)]
    [InlineData(UserRole.OrganizationUser, 400)]
    public async Task Invalid_or_unauthorized_requests_do_not_read_store(UserRole role, int expectedStatus)
    {
        var actor = RevisionAccessTests.Actor(role);
        var store = StubProxy.For<IScenarioReadStore>((_, _) => throw new Exception("Unexpected store access"));
        var scenarioId = role == UserRole.Trainee ? Guid.NewGuid() : Guid.Empty;
        var result = await new GetScenarioQueryHandler(RevisionAccessTests.Accounts(actor), store)
            .Handle(new(actor.Id, scenarioId), default);
        Assert.Equal(expectedStatus, result.Error?.Status);
    }

    [Theory]
    [InlineData(UserRole.OrganizationUser)]
    [InlineData(UserRole.PlatformAdmin)]
    public async Task Detail_uses_database_organization_scope(UserRole role)
    {
        var actor = RevisionAccessTests.Actor(role);
        var scenario = new ScenarioDetailResponse(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Drill", DateTime.UtcNow);
        var store = StubProxy.For<IScenarioReadStore>((method, args) =>
        {
            Assert.Equal(nameof(IScenarioReadStore.GetScenarioAsync), method);
            Assert.Equal(scenario.Id, args[0]);
            Assert.Equal(role == UserRole.PlatformAdmin ? null : actor.OrganizationId, args[1]);
            return Task.FromResult<ScenarioDetailResponse?>(scenario);
        });
        var result = await new GetScenarioQueryHandler(RevisionAccessTests.Accounts(actor), store)
            .Handle(new(actor.Id, scenario.Id), default);
        Assert.True(result.IsSuccess);
        Assert.Equal(scenario, result.Value);
    }

    [Fact]
    public async Task Missing_or_foreign_scenario_is_not_found()
    {
        var actor = RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var store = StubProxy.For<IScenarioReadStore>((_, _) => Task.FromResult<ScenarioDetailResponse?>(null));
        var result = await new GetScenarioQueryHandler(RevisionAccessTests.Accounts(actor), store)
            .Handle(new(actor.Id, Guid.NewGuid()), default);
        Assert.Equal(404, result.Error?.Status);
    }
}
