using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios;
using Fire3D.Application.Scenarios.Queries.GetOrganizationWorkInProgress;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class GetOrganizationWorkInProgressTests
{
    private static IScenarioReviewQueries NoReviews() => StubProxy.For<IScenarioReviewQueries>((m, _) => throw new Exception("Unexpected review read: " + m));

    [Theory]
    [InlineData(UserRole.Trainee, 1, 403)]
    [InlineData(UserRole.PlatformAdmin, 1, 403)]
    [InlineData(UserRole.OrganizationUser, 0, 400)]
    [InlineData(UserRole.OrganizationUser, 6, 400)]
    [InlineData(UserRole.OrganizationUser, -1, 400)]
    public async Task Invalid_limit_or_actor_without_tenant_never_reads_drafts(UserRole role, int limit, int status)
    {
        var actor = RevisionAccessTests.Actor(role);
        var store = StubProxy.For<IScenarioReadStore>((m, _) => throw new Exception("Unexpected store access: " + m));
        var result = await new GetOrganizationWorkInProgressQueryHandler(RevisionAccessTests.Accounts(actor), store, NoReviews())
            .Handle(new(actor.Id, actor.Id, limit), default);
        Assert.Equal(status, result.Error?.Status);
        if (status == 400) Assert.Equal(("VALIDATION_ERROR", true), (result.Error!.Code, result.Error.Errors!.ContainsKey("limit")));
    }

    [Fact]
    public async Task Items_keep_store_order_scope_and_carry_review_status_of_latest_snapshot()
    {
        var actor = RevisionAccessTests.Actor(UserRole.OrganizationUser); var family = Guid.NewGuid();
        var version = Guid.NewGuid(); var now = DateTime.UtcNow;
        var withVersion = new ScenarioDraftWorkItem(Guid.NewGuid(), 2, Guid.NewGuid(), Guid.NewGuid(), "Tower A", Guid.NewGuid(), "Drill", now, version, 3);
        var noVersion = new ScenarioDraftWorkItem(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), "Tower B", Guid.NewGuid(), "Evacuate", now.AddHours(-1), null, null);
        var store = StubProxy.For<IScenarioReadStore>((m, args) =>
        {
            Assert.Equal(nameof(IScenarioReadStore.ListRecentDraftsAsync), m);
            Assert.Equal((actor.OrganizationId, 5), ((Guid?)args[0], (int)args[1]!));
            return Task.FromResult<IReadOnlyList<ScenarioDraftWorkItem>>([withVersion, noVersion]);
        });
        var reviews = StubProxy.For<IScenarioReviewQueries>((m, args) =>
        {
            Assert.Equal(nameof(IScenarioReviewQueries.StatesAsync), m);
            Assert.Equal((actor.Id, (Guid?)family), ((Guid)args[0]!, (Guid?)args[1]));
            Assert.Equal([version], (IReadOnlyList<Guid>)args[2]!);
            return Task.FromResult(AuthResult<IReadOnlyList<ScenarioVersionReviewState>>.Ok([new(version, "Approved", Guid.NewGuid(), null)]));
        });
        var result = await new GetOrganizationWorkInProgressQueryHandler(RevisionAccessTests.Accounts(actor), store, reviews)
            .Handle(new(actor.Id, family, 5), default);
        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(new WorkInProgressItemResponse[]
        {
            new("ScenarioDraft", withVersion.BuildingId, "Tower A", withVersion.ScenarioId, "Drill", withVersion.DraftId, 2, withVersion.RevisionId, 3, "Approved", now),
            new("ScenarioDraft", noVersion.BuildingId, "Tower B", noVersion.ScenarioId, "Evacuate", noVersion.DraftId, 1, noVersion.RevisionId, null, null, now.AddHours(-1))
        }, result.Value!.Items);
    }

    [Fact]
    public async Task Empty_organization_returns_no_items_without_review_read_and_review_gate_errors_propagate()
    {
        var actor = RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var empty = StubProxy.For<IScenarioReadStore>((_, _) => Task.FromResult<IReadOnlyList<ScenarioDraftWorkItem>>([]));
        var result = await new GetOrganizationWorkInProgressQueryHandler(RevisionAccessTests.Accounts(actor), empty, NoReviews()).Handle(new(actor.Id, actor.Id), default);
        Assert.Empty(result.Value!.Items);

        var draft = new ScenarioDraftWorkItem(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), "B", Guid.NewGuid(), "S", DateTime.UtcNow, Guid.NewGuid(), 1);
        var one = StubProxy.For<IScenarioReadStore>((_, _) => Task.FromResult<IReadOnlyList<ScenarioDraftWorkItem>>([draft]));
        var revoked = StubProxy.For<IScenarioReviewQueries>((_, _) => Task.FromResult(AuthResult<IReadOnlyList<ScenarioVersionReviewState>>.Fail("UNAUTHORIZED", "Session revoked.", 401)));
        var denied = await new GetOrganizationWorkInProgressQueryHandler(RevisionAccessTests.Accounts(actor), one, revoked).Handle(new(actor.Id, actor.Id), default);
        Assert.Equal(401, denied.Error?.Status);
    }
}
