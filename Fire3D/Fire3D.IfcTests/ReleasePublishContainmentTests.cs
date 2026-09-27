using Fire3D.Infrastructure.Persistence;
using Fire3D.Infrastructure.Releases;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class ReleasePublishContainmentTests
{
    [Fact]
    public async Task Publish_fails_closed_until_the_entitlement_and_readiness_gate_exists()
    {
        await using var db = new Fire3DDbContext(new DbContextOptionsBuilder<Fire3DDbContext>().Options);
        var store = new FailClosedReleaseStore(db, TimeProvider.System);

        var result = await store.PublishAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default);

        Assert.Equal(503, result.Error?.Status);
        Assert.Equal("PUBLISH_GATE_UNAVAILABLE", result.Error?.Code);
    }
}
