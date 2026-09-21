using System.Reflection;
using Fire3D.Application.Authentication;
using Fire3D.Application.Buildings;
using Fire3D.Application.Buildings.Queries.GetRevision;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Xunit;
namespace Fire3D.IfcTests;

public class StubProxy : DispatchProxy
{
    public Func<string, object?[], object?> InvokeMethod { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) =>
        InvokeMethod(method!.Name, args ?? []);
    public static T For<T>(Func<string, object?[], object?> action) where T : class
    {
        var proxy = Create<T, StubProxy>();
        ((StubProxy)(object)proxy).InvokeMethod = action;
        return proxy;
    }
}

public class RevisionAccessTests
{
    [Theory]
    [InlineData(UserRole.Trainee, true, false, true, 403)]
    [InlineData(UserRole.OrganizationUser, false, false, true, 401)]
    [InlineData(UserRole.OrganizationUser, true, true, true, 401)]
    [InlineData(UserRole.OrganizationUser, true, false, false, 401)]
    public async Task Unavailable_or_unauthorized_actor_never_reads_revision(
        UserRole role, bool active, bool deleted, bool organizationActive, int expected)
    {
        var actor = Actor(role);
        actor.IsActive = active;
        actor.DeletedAt = deleted ? DateTime.UtcNow : null;
        var accounts = Accounts(actor, organizationActive);
        var store = StubProxy.For<IBuildingStore>((_, _) => throw new Exception("Unauthorized store access"));
        var result = await new GetRevisionQueryHandler(store, accounts)
            .Handle(new(actor.Id, Guid.NewGuid()), default);
        Assert.Equal(expected, result.Error?.Status);
    }

    [Fact]
    public async Task Missing_actor_is_unauthorized()
    {
        var store = StubProxy.For<IBuildingStore>((_, _) => throw new Exception("Unexpected store call"));
        var result = await new GetRevisionQueryHandler(store, Accounts(null))
            .Handle(new(Guid.NewGuid(), Guid.NewGuid()), default);
        Assert.Equal(401, result.Error?.Status);
    }

    [Theory]
    [InlineData(UserRole.OrganizationUser)]
    [InlineData(UserRole.PlatformAdmin)]
    public async Task Scope_is_derived_from_database_account(UserRole role)
    {
        var actor = Actor(role);
        var revision = new RevisionResponse(Guid.NewGuid(), Guid.NewGuid(), "v1", "Draft", DateTime.UtcNow, null);
        var calls = 0;
        var store = StubProxy.For<IBuildingStore>((method, args) =>
        {
            Assert.Equal(nameof(IBuildingStore.FindRevisionAsync), method);
            Assert.Equal(revision.Id, args[0]);
            Assert.Equal(role == UserRole.PlatformAdmin ? null : actor.OrganizationId, args[1]);
            calls++;
            return Task.FromResult<RevisionResponse?>(revision);
        });
        var result = await new GetRevisionQueryHandler(store, Accounts(actor))
            .Handle(new(actor.Id, revision.Id), default);
        Assert.True(result.IsSuccess);
        Assert.Equal(revision, result.Value);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Out_of_scope_or_missing_revision_returns_not_found()
    {
        var actor = Actor(UserRole.OrganizationUser);
        var store = StubProxy.For<IBuildingStore>((_, args) =>
        {
            Assert.Equal(actor.OrganizationId, args[1]);
            return Task.FromResult<RevisionResponse?>(null);
        });
        var result = await new GetRevisionQueryHandler(store, Accounts(actor))
            .Handle(new(actor.Id, Guid.NewGuid()), default);
        Assert.Equal(404, result.Error?.Status);
    }

    [Fact]
    public async Task Empty_revision_id_does_not_query_store()
    {
        var actor = Actor(UserRole.OrganizationUser);
        var store = StubProxy.For<IBuildingStore>((_, _) => throw new Exception("Unexpected store call"));
        var result = await new GetRevisionQueryHandler(store, Accounts(actor))
            .Handle(new(actor.Id, Guid.Empty), default);
        Assert.Equal(400, result.Error?.Status);
    }

    internal static User Actor(UserRole role) => new()
    {
        Id = Guid.NewGuid(), Role = role, IsActive = true,
        OrganizationId = role == UserRole.OrganizationUser ? Guid.NewGuid() : null
    };
    internal static IAuthStore Accounts(User? actor, bool organizationActive = true) =>
        StubProxy.For<IAuthStore>((method, _) => method switch
        {
            nameof(IAuthStore.FindUserAsync) => Task.FromResult(actor),
            nameof(IAuthStore.OrganizationIsActiveAsync) => Task.FromResult(organizationActive),
            _ => throw new NotSupportedException(method)
        });
}
