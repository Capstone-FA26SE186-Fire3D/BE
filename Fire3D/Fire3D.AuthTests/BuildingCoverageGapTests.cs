using System.Text.Json.Nodes;
using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using Fire3D.Application.Buildings;
using Fire3D.Application.Buildings.Commands.CreateBuilding;
using Fire3D.Application.Buildings.Commands.SetBuildingActive;
using Fire3D.Application.Buildings.Commands.UpdateBuilding;
using Fire3D.Application.Buildings.Queries.ListBuildings;
using Fire3D.Application.Content;
using Fire3D.Application.Learning;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Learning;
using Fire3D.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

// Handler and content-contract branches that need no database: authorization scope, store outcomes and nested input mapping.
public sealed class BuildingCoverageGapTests
{
    private static readonly DateTime Stored = new(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc);
    private static readonly string Tag = BuildingEtag.Format(Stored);

    private static IAuthStore Accounts(User? actor, bool organizationActive = true) => ResetProxy.For<IAuthStore>((method, _) => method switch
    {
        "FindUserAsync" => Task.FromResult(actor),
        "OrganizationIsActiveAsync" => Task.FromResult(organizationActive),
        _ => throw new InvalidOperationException(method)
    });
    private static IAuthStore NoLookups() => ResetProxy.For<IAuthStore>((method, _) => throw new InvalidOperationException("Unexpected lookup: " + method));
    private static IBuildingStore NoWrites() => ResetProxy.For<IBuildingStore>((method, _) => throw new InvalidOperationException("Unexpected persistence: " + method));
    private static T Handler<T>(string name, params object[] args) =>
        (T)Activator.CreateInstance(typeof(CreateBuildingCommand).Assembly.GetType("Fire3D.Application.Buildings." + name)!, args)!;
    private static IRequestHandler<UpdateBuildingCommand, AuthResult<BuildingResponse>> Update(IBuildingStore store, IAuthStore accounts) =>
        Handler<IRequestHandler<UpdateBuildingCommand, AuthResult<BuildingResponse>>>("Commands.UpdateBuilding.UpdateBuildingCommandHandler", store, accounts, TimeProvider.System);
    private static IRequestHandler<CreateBuildingCommand, AuthResult<BuildingResponse>> Create(IBuildingStore store, IAuthStore accounts) =>
        Handler<IRequestHandler<CreateBuildingCommand, AuthResult<BuildingResponse>>>("Commands.CreateBuilding.CreateBuildingCommandHandler", store, accounts, TimeProvider.System);
    private static IRequestHandler<SetBuildingActiveCommand, AuthResult<BuildingSummaryResponse>> SetActive(IBuildingStore store, IAuthStore accounts) =>
        Handler<IRequestHandler<SetBuildingActiveCommand, AuthResult<BuildingSummaryResponse>>>("Commands.SetBuildingActive.SetBuildingActiveCommandHandler", store, accounts, TimeProvider.System);
    private static IRequestHandler<ListBuildingsQuery, AuthResult<PageResponse<BuildingSummaryResponse>>> List(IBuildingStore store, IAuthStore accounts) =>
        Handler<IRequestHandler<ListBuildingsQuery, AuthResult<PageResponse<BuildingSummaryResponse>>>>("Queries.ListBuildings.ListBuildingsQueryHandler", store, accounts);

    private static User Owner(Guid org) => new() { Id = Guid.NewGuid(), OrganizationId = org, Role = UserRole.OrganizationUser, IsActive = true };
    private static User Admin() => new() { Id = Guid.NewGuid(), Role = UserRole.PlatformAdmin, IsActive = true };
    private static Building Stored_(Guid org, bool active = true) => new()
    {
        Id = Guid.NewGuid(), OrganizationId = org, Name = "Office", TotalFloors = 1, IsActive = active, CreatedAt = Stored, UpdatedAt = Stored
    };

    [Fact]
    public async Task Update_rejects_invalid_input_before_any_account_or_building_lookup()
    {
        var result = await Update(NoWrites(), NoLookups()).Handle(new(Guid.NewGuid(), null, Guid.NewGuid(), new("  ", null, 0, null, null), Tag), default);
        Assert.Equal(("VALIDATION_ERROR", 400), (result.Error?.Code, result.Error?.Status));
        Assert.NotEmpty(result.Error!.Errors!);
    }

    [Fact]
    public async Task Admin_cannot_edit_or_archive_a_building_whose_organization_is_disabled()
    {
        var admin = Admin();
        var building = Stored_(Guid.NewGuid());
        var store = ResetProxy.For<IBuildingStore>((method, _) => method == nameof(IBuildingStore.FindBuildingAsync)
            ? Task.FromResult<Building?>(building) : throw new InvalidOperationException("Unexpected persistence: " + method));
        var update = await Update(store, Accounts(admin, organizationActive: false)).Handle(new(admin.Id, null, building.Id, new("Renamed", null, 2, null, null), Tag), default);
        Assert.Equal(("FORBIDDEN", 403), (update.Error?.Code, update.Error?.Status));
        var archive = await SetActive(store, Accounts(admin, organizationActive: false)).Handle(new(admin.Id, null, building.Id, false, Tag), default);
        Assert.Equal(("FORBIDDEN", 403), (archive.Error?.Code, archive.Error?.Status));
    }

    [Fact]
    public async Task Update_sends_trimmed_location_and_contact_with_the_if_match_version_to_the_store()
    {
        var org = Guid.NewGuid(); var owner = Owner(org); var building = Stored_(org);
        BuildingLocation? location = null; BuildingContact? contact = null; long? expected = null;
        var store = ResetProxy.For<IBuildingStore>((method, args) =>
        {
            switch (method)
            {
                case nameof(IBuildingStore.FindBuildingAsync):
                    building.BuildingLocation = location; building.BuildingContact = contact;
                    return Task.FromResult<Building?>(building);
                case nameof(IBuildingStore.UpdateBuildingWithAuditAsync):
                    location = (BuildingLocation?)args[1]; contact = (BuildingContact?)args[2]; expected = (long?)args[6];
                    return Task.FromResult(BuildingWriteOutcome.Written);
                default: throw new InvalidOperationException(method);
            }
        });
        var request = new UpdateBuildingRequest(" Tower ", " Office ", 4, new(" 1 Main St ", " Hanoi ", " Ba Dinh ", 21.03m, 105.85m, null),
            new(" Lan ", " Manager ", " 0900000000 ", " lan@example.test ", true));
        var result = await Update(store, Accounts(owner)).Handle(new(owner.Id, org, building.Id, request, Tag), default);
        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.Equal(BuildingEtag.Version(Stored), expected);
        Assert.Equal((building.Id, "1 Main St", "Hanoi", "Ba Dinh", 21.03m, 105.85m), (location!.BuildingId, location.Address, location.City, location.District, location.Latitude, location.Longitude));
        Assert.Equal((building.Id, "Lan", "Manager", "0900000000", "lan@example.test", true), (contact!.BuildingId, contact.ContactName, contact.ContactRole, contact.Phone, contact.Email, contact.IsPrimary));
        Assert.Equal(("Tower", "Office", 4), (result.Value!.Name, result.Value.BuildingType, result.Value.TotalFloors));
        Assert.Equal(("1 Main St", "Lan"), (result.Value.Location!.Address, result.Value.Contact!.ContactName));
    }

    [Theory]
    [InlineData(BuildingWriteOutcome.Stale, "PRECONDITION_FAILED", 412)]
    [InlineData(BuildingWriteOutcome.Rejected, "BUILDING_MUTATION_FAILED", 409)]
    public async Task Update_maps_a_store_outcome_that_did_not_write(BuildingWriteOutcome outcome, string code, int status)
    {
        var org = Guid.NewGuid(); var owner = Owner(org); var building = Stored_(org);
        var store = ResetProxy.For<IBuildingStore>((method, _) => method switch
        {
            nameof(IBuildingStore.FindBuildingAsync) => Task.FromResult<Building?>(building),
            nameof(IBuildingStore.UpdateBuildingWithAuditAsync) => Task.FromResult(outcome),
            _ => throw new InvalidOperationException(method)
        });
        var result = await Update(store, Accounts(owner)).Handle(new(owner.Id, org, building.Id, new("Renamed", null, 2, null, null), Tag), default);
        Assert.Equal((code, status), (result.Error?.Code, result.Error?.Status));
    }

    [Theory]
    [InlineData(BuildingWriteOutcome.Stale, "PRECONDITION_FAILED", 412)]
    [InlineData(BuildingWriteOutcome.Rejected, "BUILDING_MUTATION_FAILED", 409)]
    public async Task Archive_maps_a_store_outcome_when_the_building_is_still_active(BuildingWriteOutcome outcome, string code, int status)
    {
        var org = Guid.NewGuid(); var owner = Owner(org); var building = Stored_(org);
        var store = ResetProxy.For<IBuildingStore>((method, _) => method switch
        {
            nameof(IBuildingStore.FindBuildingAsync) => Task.FromResult<Building?>(building),
            nameof(IBuildingStore.SetBuildingActiveWithAuditAsync) => Task.FromResult(outcome),
            _ => throw new InvalidOperationException(method)
        });
        var result = await SetActive(store, Accounts(owner)).Handle(new(owner.Id, org, building.Id, false, Tag), default);
        Assert.Equal((code, status), (result.Error?.Code, result.Error?.Status));
    }

    [Fact]
    public async Task Create_sends_a_trimmed_contact_returns_it_and_maps_a_rejected_store_write()
    {
        var org = Guid.NewGuid(); var owner = Owner(org);
        BuildingContact? stored = null; var outcome = BuildingWriteOutcome.Written;
        var store = ResetProxy.For<IBuildingStore>((method, args) =>
        {
            if (method != nameof(IBuildingStore.CreateBuildingOnceAsync)) throw new InvalidOperationException(method);
            stored = (BuildingContact?)args[2];
            return Task.FromResult((outcome, ((Building)args[0]!).Id));
        });
        var request = new CreateBuildingRequest("Office", null, 1, null, new(" Lan ", null, " 0900000000 ", null, false));
        var created = await Create(store, Accounts(owner)).Handle(new(owner.Id, org, request, "create-1"), default);
        Assert.True(created.IsSuccess, created.Error?.Code);
        Assert.Equal(("Lan", "0900000000", false), (stored!.ContactName, stored.Phone, stored.IsPrimary));
        Assert.Equal((stored.Id, "Lan"), (created.Value!.Contact!.Id, created.Value.Contact.ContactName));
        Assert.Null(created.Value.Location);
        outcome = BuildingWriteOutcome.Rejected;
        var rejected = await Create(store, Accounts(owner)).Handle(new(owner.Id, org, request, "create-2"), default);
        Assert.Equal(("BUILDING_MUTATION_FAILED", 409), (rejected.Error?.Code, rejected.Error?.Status));
    }

    [Theory]
    [InlineData(0, 20, null)]
    [InlineData(1, 101, null)]
    [InlineData(1, 20, 201)]
    public async Task List_rejects_paging_and_search_limits_before_any_lookup(int page, int pageSize, int? searchLength)
    {
        var result = await List(NoWrites(), NoLookups()).Handle(new(Guid.NewGuid(), null, new(page, pageSize, searchLength is int n ? new string('a', n) : null, null)), default);
        Assert.Equal(("VALIDATION_ERROR", 400), (result.Error?.Code, result.Error?.Status));
    }

    public static TheoryData<string, string, int> ScopeCases => new()
    {
        { "missing", "FORBIDDEN", 403 }, { "inactive", "FORBIDDEN", 403 }, { "empty", "VALIDATION_ERROR", 400 },
        { "admin-disabled-org", "FORBIDDEN", 403 }, { "trainee", "FORBIDDEN", 403 }, { "other-tenant", "FORBIDDEN", 403 }
    };

    [Theory]
    [MemberData(nameof(ScopeCases))]
    public async Task List_scope_requires_an_active_account_and_its_own_live_organization(string scenario, string code, int status)
    {
        var org = Guid.NewGuid();
        User? actor = scenario switch
        {
            "missing" => null,
            "inactive" => Owner(org),
            "admin-disabled-org" => Admin(),
            "trainee" => new User { Id = Guid.NewGuid(), Role = UserRole.Trainee, IsActive = true },
            _ => Owner(org)
        };
        if (scenario == "inactive") actor!.IsActive = false;
        Guid? requested = scenario switch { "empty" => Guid.Empty, "admin-disabled-org" or "other-tenant" => Guid.NewGuid(), _ => null };
        var result = await List(NoWrites(), Accounts(actor, organizationActive: scenario != "admin-disabled-org"))
            .Handle(new(actor?.Id ?? Guid.NewGuid(), requested, new(1, 20, null, null)), default);
        Assert.Equal((code, status), (result.Error?.Code, result.Error?.Status));
        if (scenario == "empty") Assert.Equal("organizationId cannot be empty.", result.Error!.Message);
    }

    [Fact]
    public async Task List_for_an_organization_user_reads_only_its_own_tenant()
    {
        var org = Guid.NewGuid(); var owner = Owner(org); Guid? scope = Guid.Empty;
        var store = ResetProxy.For<IBuildingStore>((method, args) =>
        {
            Assert.Equal(nameof(IBuildingStore.ListBuildingsAsync), method);
            scope = (Guid?)args[0];
            return Task.FromResult(new PageResponse<BuildingSummaryResponse>([], 0, 1, 20));
        });
        Assert.True((await List(store, Accounts(owner)).Handle(new(owner.Id, null, new(1, 20, "tower", null)), default)).IsSuccess);
        Assert.Equal(org, scope);
    }

    // fet3d.learn/1 blocks beyond heading/paragraph/video: callout tone, list items and image url+alt; sources need a known role.
    [Fact]
    public void Learn_content_normalizes_callout_list_and_image_blocks()
    {
        var blocks = new JsonArray(
            JsonNode.Parse("""{"type":"callout","tone":"warning","text":" Keep low "}"""),
            JsonNode.Parse("""{"type":"list","ordered":true,"items":[" Exit ","Call 114"]}"""),
            JsonNode.Parse("""{"type":"image","url":"https://example.test/exit.png","alt":" Exit sign "}"""));
        var (issues, content) = LearnContent.Normalize(new("Article", "Title", "Summary", null, blocks, null, [new(Guid.NewGuid(), "Transcript")]));
        Assert.Empty(issues);
        var normalized = content!["blocks"]!.AsArray();
        Assert.Equal("""{"type":"callout","tone":"warning","text":"Keep low"}""", normalized[0]!.ToJsonString());
        Assert.Equal("""{"type":"list","ordered":true,"items":["Exit","Call 114"]}""", normalized[1]!.ToJsonString());
        Assert.Equal("""{"type":"image","url":"https://example.test/exit.png","alt":"Exit sign"}""", normalized[2]!.ToJsonString());
    }

    [Fact]
    public void Learn_content_reports_invalid_callout_list_image_and_source_role()
    {
        var blocks = new JsonArray(
            JsonNode.Parse("""{"type":"callout","tone":"loud","text":"x"}"""),
            JsonNode.Parse("""{"type":"list","items":[]}"""),
            JsonNode.Parse("""{"type":"list","items":[3]}"""),
            JsonNode.Parse("""{"type":"image","url":"http://example.test/a.png","alt":"a"}"""));
        var (issues, content) = LearnContent.Normalize(new("Article", "Title", "Summary", null, blocks, null, [new(Guid.NewGuid(), "Author")]));
        Assert.Null(content);
        var found = issues.Select(i => (i.Code, i.Path)).ToArray();
        Assert.Contains(("VALUE_UNSUPPORTED", "$.blocks[0].tone"), found);
        Assert.Contains(("FIELD_REQUIRED", "$.blocks[1].items"), found);
        Assert.Contains(("FIELD_REQUIRED", "$.blocks[2].items[0]"), found);
        Assert.Contains(("URL_INVALID", "$.blocks[3].url"), found);
        Assert.Contains(("SOURCES_INVALID", "$.sources"), found);
    }

    [Fact]
    public void Library_content_rejects_non_text_description_and_nested_equipment_properties()
    {
        var issues = LibraryContent.Validate("Equipment", new("CO2", JsonNode.Parse("""{"description":5,"properties":{"weightKg":5,"size":{"h":1}}}""")!.AsObject()));
        Assert.Contains(issues, i => (i.Code, i.Path) == ("STRING_INVALID", "$.payload.description"));
        Assert.Contains(issues, i => (i.Code, i.Path) == ("PROPERTIES_INVALID", "$.payload.properties"));
        Assert.Empty(LibraryContent.Validate("Equipment", new("CO2", JsonNode.Parse("""{"description":"Portable","properties":{"weightKg":5,"rated":true}}""")!.AsObject())));
    }

    // Learner input checks run before the database gate, so they need no connection.
    [Fact]
    public async Task Learner_complete_and_reconcile_reject_invalid_input_without_a_database_call()
    {
        await using var db = new Fire3DDbContext(new DbContextOptionsBuilder<Fire3DDbContext>().UseNpgsql("Host=invalid.test;Database=none").Options);
        var store = new LearnerSessions(db, Options.Create(new LearnerSessionOptions()));
        var caller = new LearnerCaller(Guid.NewGuid(), Guid.NewGuid(), null);
        var negative = await store.CompleteAsync(caller, Guid.NewGuid(), new(-1, "Finished"), "k", default);
        Assert.Equal(("VALIDATION_ERROR", 422), (negative.Error?.Code, negative.Error?.Status));
        Assert.Equal("VALIDATION_ERROR", (await store.CompleteAsync(caller, Guid.NewGuid(), new(1, "Paused"), "k", default)).Error?.Code);
        var empty = await store.ReconcileAsync(caller, new([], []), default);
        Assert.Equal(("VALIDATION_ERROR", 400), (empty.Error?.Code, empty.Error?.Status));
        Assert.Equal("VALIDATION_ERROR", (await store.ReconcileAsync(caller, new(Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray(), null), default)).Error?.Code);
        Assert.Equal("VALIDATION_ERROR", (await store.ReconcileAsync(caller, new(null, [new string('k', 129)]), default)).Error?.Code);
    }
}
