using Fire3D.Application.Scenarios;
using Fire3D.Infrastructure.Scenarios;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // Issue #85: the Web finds its playtests again, checks availability before preparing and knows when a handoff is allowed.
    [PostgresFact]
    public async Task Playtest_list_availability_and_handoff_flags_are_owner_scoped()
    {
        var f = await SeedPlaytestRuntime(units: 1);
        var stranger = await SeedReadyPackage("PlaytestPackage"); var strangerFamily = await SeedPlaytestFamily(stranger.Owner);
        await WithPlaytestRuntimeGates(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new PlaytestLifecycle(db, Options.Create(HandoffOptions));
            var ready = (await store.AvailabilityAsync(f.Owner, f.Web, f.Scenario, f.Version, default)).Value!;
            Assert.Equal(("Trial", 1, true, true), (ready.Entitlement.Kind, ready.Entitlement.TrialUnitsRemaining, ready.PackageReady, ready.CanCreate));
            Assert.Empty(ready.Blockers);
            var unconfigured = (await new PlaytestLifecycle(db, Options.Create(new PlaytestOptions())).AvailabilityAsync(f.Owner, f.Web, f.Scenario, null, default)).Value!;
            Assert.Equal(new[] { "PLAYTEST_LAUNCH_UNAVAILABLE", "PLAYTEST_HANDOFF_UNAVAILABLE" }, unconfigured.Blockers);
            Assert.Null(unconfigured.PackageReady);
            Assert.Equal("NOT_FOUND", (await store.AvailabilityAsync(f.Owner, f.Web, f.Scenario, Guid.NewGuid(), default)).Error?.Code);

            var first = (await store.PrepareAsync(f.Owner, f.Web, f.Building, f.Scenario, new(f.Revision, ScenarioVersionId: f.Version), "reads-1", default)).Value!.Id;
            var second = (await store.PrepareAsync(f.Owner, f.Web, f.Building, f.Scenario, new(f.Revision, ScenarioVersionId: f.Version), "reads-2", default)).Value!.Id;
            await ExecuteAsync($"UPDATE playtest_sessions SET created_at=now()-interval '1 minute' WHERE id='{first}'");
            var page = (await store.ListAsync(f.Owner, f.Web, f.Scenario, f.Version, null, 1, 20, default)).Value!;
            Assert.Equal(2, page.Total);
            Assert.Equal(new[] { second, first }, page.Items.Select(x => x.Id));
            Assert.All(page.Items, x => Assert.Equal(("Created", f.Version), (x.Status, x.ScenarioVersionId)));
            Assert.Equal(2, (await store.ListAsync(f.Owner, f.Web, f.Scenario, null, "created", 1, 20, default)).Value!.Total);
            Assert.Equal(0, (await store.ListAsync(f.Owner, f.Web, f.Scenario, null, "Completed", 1, 20, default)).Value!.Total);
            var paged = (await store.ListAsync(f.Owner, f.Web, f.Scenario, null, null, 2, 1, default)).Value!;
            Assert.Equal((2, first), (paged.Total, paged.Items.Single().Id));
            Assert.Equal("VALIDATION_ERROR", (await store.ListAsync(f.Owner, f.Web, f.Scenario, null, "Done", 1, 20, default)).Error?.Code);
            Assert.Equal("NOT_FOUND", (await store.ListAsync(stranger.Owner, strangerFamily, f.Scenario, null, null, 1, 20, default)).Error?.Code);
            Assert.Equal("FORBIDDEN", (await store.ListAsync(adminId, await SeedPlaytestFamily(adminId), f.Scenario, null, null, 1, 20, default)).Error?.Code);

            // A second tab rotating the code is visible through openHandoffId; the first tab's code is no longer current.
            var state = (await store.GetAsync(f.Owner, f.Web, second, default)).Value!;
            Assert.True(state.Recovery.CanIssueHandoff); Assert.Null(state.OpenHandoffId);
            var tabA = (await store.CreateHandoffAsync(f.Owner, f.Web, second, "reads-tab-a", default)).Value!;
            Assert.Equal(tabA.HandoffId, (await store.GetAsync(f.Owner, f.Web, second, default)).Value!.OpenHandoffId);
            var tabB = (await store.CreateHandoffAsync(f.Owner, f.Web, second, "reads-tab-b", default)).Value!;
            Assert.Equal(tabB.HandoffId, (await store.GetAsync(f.Owner, f.Web, second, default)).Value!.OpenHandoffId);

            // Once the only Trial unit is used, availability reports the entitlement blocker before any QR is shown.
            await ExecuteAsync($"UPDATE service_entitlements SET playtest_units_used=playtest_units_granted WHERE id='{f.Trial}'");
            var exhausted = (await store.AvailabilityAsync(f.Owner, f.Web, f.Scenario, f.Version, default)).Value!;
            Assert.Equal(new[] { "PLAYTEST_ENTITLEMENT_REQUIRED" }, exhausted.Blockers);
            Assert.False(exhausted.CanCreate); Assert.Equal(0, exhausted.Entitlement.TrialUnitsRemaining);
        });
    }
}
