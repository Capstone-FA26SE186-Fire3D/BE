using Fire3D.Application.Scenarios;
using Fire3D.Application.Scenarios.Queries.GetScenarioVersion;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class GetScenarioVersionTests
{
    [Theory]
    [InlineData(UserRole.Trainee, 403)]
    [InlineData(UserRole.OrganizationUser, 400)]
    public async Task Invalid_or_unauthorized_requests_do_not_read_store(UserRole role, int expectedStatus)
    {
        var actor = RevisionAccessTests.Actor(role);
        var store = StubProxy.For<IScenarioReadStore>((_, _) => throw new Exception("Unexpected store access"));
        var versionId = role == UserRole.Trainee ? Guid.NewGuid() : Guid.Empty;

        var result = await new GetScenarioVersionQueryHandler(RevisionAccessTests.Accounts(actor), store)
            .Handle(new(actor.Id, versionId), default);

        Assert.Equal(expectedStatus, result.Error?.Status);
    }

    [Theory]
    [InlineData(UserRole.OrganizationUser)]
    [InlineData(UserRole.PlatformAdmin)]
    public async Task Detail_uses_database_organization_scope(UserRole role)
    {
        var actor = RevisionAccessTests.Actor(role);
        var versionId = Guid.NewGuid();
        var store = StubProxy.For<IScenarioReadStore>((method, args) =>
        {
            Assert.Equal(nameof(IScenarioReadStore.GetScenarioVersionAsync), method);
            Assert.Equal(versionId, args[0]);
            Assert.Equal(role == UserRole.PlatformAdmin ? null : actor.OrganizationId, args[1]);
            return Task.FromResult<ScenarioVersionDetailResponse?>(null);
        });

        var result = await new GetScenarioVersionQueryHandler(RevisionAccessTests.Accounts(actor), store)
            .Handle(new(actor.Id, versionId), default);

        Assert.Equal(404, result.Error?.Status);
    }
}
