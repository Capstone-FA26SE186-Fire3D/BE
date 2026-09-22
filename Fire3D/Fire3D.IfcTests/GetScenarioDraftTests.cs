using System.Text.Json.Nodes;
using Fire3D.Application.Scenarios;
using Fire3D.Application.Scenarios.Queries.GetScenarioDraft;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class GetScenarioDraftTests
{
    [Theory]
    [InlineData(UserRole.Trainee, 403)]
    [InlineData(UserRole.OrganizationUser, 400)]
    public async Task Invalid_or_unauthorized_requests_do_not_read_store(UserRole role, int expectedStatus)
    {
        var actor = RevisionAccessTests.Actor(role);
        var store = StubProxy.For<IScenarioReadStore>((_, _) => throw new Exception("Unexpected store access"));
        var id = role == UserRole.Trainee ? Guid.NewGuid() : Guid.Empty;
        var result = await new GetScenarioDraftQueryHandler(RevisionAccessTests.Accounts(actor), store).Handle(new(actor.Id, id), default);
        Assert.Equal(expectedStatus, result.Error?.Status);
    }

    [Fact]
    public async Task Detail_preserves_scope_and_version()
    {
        var actor = RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var draft = new ScenarioDraftResponse(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), actor.OrganizationId!.Value,
            1, new JsonObject(), "manual", null, DateTime.UtcNow, DateTime.UtcNow, 42);
        var store = StubProxy.For<IScenarioReadStore>((method, args) =>
        {
            Assert.Equal(nameof(IScenarioReadStore.GetScenarioDraftAsync), method);
            Assert.Equal(actor.OrganizationId, args[1]);
            return Task.FromResult<ScenarioDraftResponse?>(draft);
        });
        var result = await new GetScenarioDraftQueryHandler(RevisionAccessTests.Accounts(actor), store).Handle(new(actor.Id, draft.Id), default);
        Assert.True(result.IsSuccess); Assert.Equal(42u, result.Value?.Version);
    }
}
