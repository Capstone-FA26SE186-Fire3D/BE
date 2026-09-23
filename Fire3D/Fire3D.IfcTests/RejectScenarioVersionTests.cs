using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios.Commands.RejectScenarioVersion;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class RejectScenarioVersionTests
{
    [Theory]
    [InlineData(UserRole.Trainee, 403)]
    [InlineData(UserRole.OrganizationUser, 400)]
    public async Task Invalid_or_unauthorized_requests_do_not_write(UserRole role, int expectedStatus)
    {
        var actor = RevisionAccessTests.Actor(role);
        var store = StubProxy.For<IScenarioReviewStore>((_, _) => throw new Exception("Unexpected store access"));
        var request = role == UserRole.Trainee
            ? new RejectScenarioVersionRequest(Guid.NewGuid(), Guid.NewGuid(), "Invalid egress route")
            : new RejectScenarioVersionRequest(Guid.Empty, Guid.NewGuid(), "Invalid egress route");
        var result = await new RejectScenarioVersionCommandHandler(RevisionAccessTests.Accounts(actor), store)
            .Handle(new(actor.Id, Guid.NewGuid(), request), default);
        Assert.Equal(expectedStatus, result.Error?.Status);
    }

    [Theory]
    [InlineData(UserRole.OrganizationUser)]
    [InlineData(UserRole.PlatformAdmin)]
    public async Task Rejection_uses_database_organization_scope(UserRole role)
    {
        var actor = RevisionAccessTests.Actor(role);
        var revisionId = Guid.NewGuid();
        var request = new RejectScenarioVersionRequest(Guid.NewGuid(), Guid.NewGuid(), "Invalid egress route");
        var store = StubProxy.For<IScenarioReviewStore>((method, args) =>
        {
            Assert.Equal(nameof(IScenarioReviewStore.CreateRejectedReviewAsync), method);
            Assert.Equal(revisionId, args[1]);
            Assert.Equal(role == UserRole.PlatformAdmin ? null : actor.OrganizationId, args[2]);
            return Task.FromResult(AuthResult<Guid>.Ok(Guid.NewGuid()));
        });
        var result = await new RejectScenarioVersionCommandHandler(RevisionAccessTests.Accounts(actor), store)
            .Handle(new(actor.Id, revisionId, request), default);
        Assert.True(result.IsSuccess);
    }
}
