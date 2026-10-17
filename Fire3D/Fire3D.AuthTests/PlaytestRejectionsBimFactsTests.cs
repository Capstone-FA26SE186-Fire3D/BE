using Fire3D.Application.Ifc;
using Fire3D.Infrastructure.Ifc;
using Fire3D.Infrastructure.Scenarios;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // BE#120: the Web reads which runtime events of a playtest were refused, without calling events:batch.
    [PostgresFact]
    public async Task Playtest_status_lists_rejected_runtime_events_for_the_web()
    {
        var f = await SeedPlaytestRuntime();
        await DeclareAlarmEvent();
        await WithPlaytestRuntimeGates(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var store = new PlaytestLifecycle(db, Options.Create(HandoffOptions));
            var id = (await store.PrepareAsync(f.Owner, f.Web, f.Building, f.Scenario, new(f.Revision, ScenarioVersionId: f.Version), "rej-prepare", default)).Value!.Id;
            var started = await store.StartAsync(f.Owner, f.Web, id, new("1.0.0"), "rej-start", default);
            Assert.True(started.IsSuccess, started.Error?.Code);
            Assert.True((await store.LaunchedAsync(f.Owner, f.Web, id, new(started.Value!.Generation), default)).IsSuccess);
            Assert.Equal(0, (await store.GetAsync(f.Owner, f.Web, id, default)).Value!.RejectedEvents!.Total);

            await store.RecordEventsAsync(f.Owner, f.Web, id, new([Event(1), Event(2, type: "Teleported"),
                Event(3, payload: "{\"floor\":2}", type: "AlarmRaised"), Event(4, payload: "{\"objectId\":\"call-1\"}", type: "AlarmRaised")]), default);
            var rejected = (await store.GetAsync(f.Owner, f.Web, id, default)).Value!.RejectedEvents!;
            Assert.Equal(2, rejected.Total);
            Assert.Equal(1, rejected.ByCode["EVENT_TYPE_UNSUPPORTED"]);
            Assert.Equal(1, rejected.ByCode["EVENT_PAYLOAD_INVALID"]);
            Assert.Equal([3L, 2L], rejected.Recent.Select(x => x.Sequence));
            Assert.Equal(("AlarmRaised", "EVENT_PAYLOAD_INVALID"), (rejected.Recent[0].Type, rejected.Recent[0].Code));
        });
    }

    // BE#120: the three facts of a room stay on one page (stable order), and rooms can be filtered or read one row per room.
    [PostgresFact]
    public async Task Bim_facts_keep_a_room_together_and_filter_unnamed_rooms()
    {
        var revision = await SeedVerifiedIfcRevision();
        string Id(char c) => new(c, 22);
        var values = new List<string>();
        foreach (var (space, name, noName) in new[] { (Id('C'), "\"Kitchen\"", false), (Id('A'), "null", true), (Id('B'), "\"Office\"", false) })
            foreach (var path in new[] { "Name", "LongName", "Category" })
                values.Add($"(gen_random_uuid(),'{revision}','{space}','IfcSpace','{path}',{(path == "Name" ? $"'{name}'::jsonb" : "NULL")},repeat('d',64),'{(path == "Name" && noName ? "[\"SPACE_UNNAMED\"]" : "[]")}'::jsonb,'2026-10-18T00:00:00Z')");
        values.Add($"(gen_random_uuid(),'{revision}','{Id('D')}','IfcDoor','Name','\"Exit\"'::jsonb,repeat('d',64),'[]'::jsonb,'2026-10-18T00:00:00Z')");
        await ExecuteAsync($"INSERT INTO bim_facts(id,revision_id,ifc_global_id,entity_type,property_path,value,source_hash,quality_flags,created_at) VALUES {string.Join(",", values)}");

        await using var db = BuildingContext(testConnection);
        var store = new IfcReadStore(db);
        var first = (await store.ListBimFactsAsync(revision, null, new(), 1, 3, default))!;
        Assert.Equal(10, first.TotalCount);
        Assert.All(first.Items, x => Assert.Equal(Id('A'), x.IfcGlobalId));
        Assert.Equal(["Category", "LongName", "Name"], first.Items.Select(x => x.PropertyPath));

        var unnamed = (await store.ListBimFactsAsync(revision, null, new(QualityFlag: "SPACE_UNNAMED"), 1, 20, default))!;
        Assert.Equal(3, unnamed.TotalCount);
        Assert.All(unnamed.Items, x => Assert.Equal(Id('A'), x.IfcGlobalId));
        Assert.Equal(1, (await store.ListBimFactsAsync(revision, null, new(EntityType: "IfcDoor"), 1, 20, default))!.TotalCount);
        Assert.Equal(3, (await store.ListBimFactsAsync(revision, null, new(IfcGlobalId: Id('B')), 1, 20, default))!.TotalCount);

        var rooms = (await store.ListBimFactElementsAsync(revision, null, new(EntityType: "IfcSpace"), 1, 20, default))!;
        Assert.Equal([Id('A'), Id('B'), Id('C')], rooms.Items.Select(x => x.IfcGlobalId));
        Assert.Equal(["SPACE_UNNAMED"], rooms.Items[0].QualityFlags);
        Assert.Equal("Office", rooms.Items[1].Name!.Value.GetString());
        var onlyUnnamed = (await store.ListBimFactElementsAsync(revision, null, new(QualityFlag: "SPACE_UNNAMED"), 1, 20, default))!;
        Assert.Equal(Id('A'), Assert.Single(onlyUnnamed.Items).IfcGlobalId);
    }
}
