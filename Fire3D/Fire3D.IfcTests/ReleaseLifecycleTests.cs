using Fire3D.Application.Authentication;
using Fire3D.Application.Releases;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class ReleaseLifecycleTests
{
    [Theory]
    [InlineData(UserRole.OrganizationUser)]
    [InlineData(UserRole.PlatformAdmin)]
    public async Task Build_uses_database_tenant_scope(UserRole role)
    {
        var actor = RevisionAccessTests.Actor(role);
        var request = ValidBuild();
        var expected = Response(request, actor.OrganizationId ?? Guid.NewGuid());
        var store = StubProxy.For<IReleaseStore>((method, args) =>
        {
            Assert.Equal(nameof(IReleaseStore.BuildAsync), method);
            Assert.Equal(actor.Id, args[0]);
            Assert.Equal(role == UserRole.PlatformAdmin ? null : actor.OrganizationId, args[1]);
            Assert.Same(request, args[2]);
            return Task.FromResult(AuthResult<ReleaseResponse>.Ok(expected));
        });

        var result = await new BuildReleaseHandler(RevisionAccessTests.Accounts(actor), store)
            .Handle(new(actor.Id, request), default);

        Assert.Same(expected, result.Value);
    }

    [Fact]
    public async Task Build_rejects_invalid_package_before_store()
    {
        var actor = RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var invalid = ValidBuild() with { ChecksumSha256 = "not-a-sha256" };
        var store = StubProxy.For<IReleaseStore>((_, _) => throw new Exception("Store must not be reached"));

        var result = await new BuildReleaseHandler(RevisionAccessTests.Accounts(actor), store)
            .Handle(new(actor.Id, invalid), default);

        Assert.Equal(400, result.Error?.Status);
        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
    }

    [Fact]
    public async Task Trainee_cannot_manage_releases()
    {
        var actor = RevisionAccessTests.Actor(UserRole.Trainee);
        var store = StubProxy.For<IReleaseStore>((_, _) => throw new Exception("Store must not be reached"));

        var result = await new BuildReleaseHandler(RevisionAccessTests.Accounts(actor), store)
            .Handle(new(actor.Id, ValidBuild()), default);

        Assert.Equal(403, result.Error?.Status);
    }

    [Fact]
    public async Task Get_hides_missing_or_foreign_release()
    {
        var actor = RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var store = StubProxy.For<IReleaseStore>((method, args) =>
        {
            Assert.Equal(nameof(IReleaseStore.GetAsync), method);
            Assert.Equal(actor.OrganizationId, args[1]);
            return Task.FromResult<ReleaseResponse?>(null);
        });

        var result = await new GetReleaseHandler(RevisionAccessTests.Accounts(actor), store)
            .Handle(new(actor.Id, Guid.NewGuid()), default);

        Assert.Equal(404, result.Error?.Status);
    }

    [Fact]
    public async Task Revoke_requires_reason_and_trims_it()
    {
        var actor = RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var releaseId = Guid.NewGuid();
        var calls = 0;
        var store = StubProxy.For<IReleaseStore>((method, args) =>
        {
            calls++;
            Assert.Equal(nameof(IReleaseStore.RevokeAsync), method);
            Assert.Equal("unsafe package", args[3]);
            return Task.FromResult(AuthResult<bool>.Ok(true));
        });
        var handler = new RevokeReleaseHandler(RevisionAccessTests.Accounts(actor), store);

        var invalid = await handler.Handle(new(actor.Id, releaseId, new("   ")), default);
        var valid = await handler.Handle(new(actor.Id, releaseId, new("  unsafe package  ")), default);

        Assert.Equal(400, invalid.Error?.Status);
        Assert.True(valid.IsSuccess);
        Assert.Equal(1, calls);
    }

    private static BuildReleaseRequest ValidBuild() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{}",
        "releases/manifest.json", new string('a', 64), "releases/package.zip", new string('b', 64),
        1024, "1.0.0", "1.0", "Android");

    private static ReleaseResponse Response(BuildReleaseRequest request, Guid organizationId) => new(
        Guid.NewGuid(), request.RevisionId, request.ScenarioVersionId, Guid.NewGuid(), organizationId,
        request.ConfirmationReviewId, "Built", request.SafetyThresholds, null, null, null, null, null,
        DateTime.UtcNow, DateTime.UtcNow,
        new(Guid.NewGuid(), request.CandidateArtifactId, request.ManifestUrl, request.ManifestSha256,
            request.PackageUrl, request.ChecksumSha256, request.PackageSizeBytes, request.MinRuntimeVersion,
            request.SchemaVersion, request.BuildTarget));
}
