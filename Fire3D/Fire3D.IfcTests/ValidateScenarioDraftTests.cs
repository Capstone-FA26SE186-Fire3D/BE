using System.Text.Json.Nodes;
using Fire3D.Application.Scenarios;
using Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class ValidateScenarioDraftTests
{
    [Fact]
    public void Structural_validator_reports_missing_required_configuration()
    {
        var issues = ScenarioDraftStructuralValidator.Validate(JsonNode.Parse("{}")!);
        Assert.Contains(issues, issue => issue.Code == "SPAWN_REQUIRED");
        Assert.Contains(issues, issue => issue.Code == "ROUTE_REQUIRED");
        Assert.Contains(issues, issue => issue.Code == "SCORING_REQUIRED");
    }

    [Fact]
    public void Structural_validator_accepts_a_valid_draft()
    {
        var state = JsonNode.Parse("""
            {"spawnPoints":[{"x":1,"y":2,"z":3,"rotation":0}],"hazards":[],"scoringConfig":{"baseScore":100,"timeLimitSeconds":60,"penaltyPerMistake":5},"routingConfig":{"evacuationRoutes":["exit-a"]},"learningObjectives":["Evacuate"],"learnerInstructions":"Follow exits","rubric":{"schema_version":"1","pass_threshold":1,"criteria":[{"id":"exit","metric":"exit","mandatory":true,"weight":1,"operator":"gte","threshold":1}]}}
            """)!;
        Assert.Empty(ScenarioDraftStructuralValidator.Validate(state));
    }

    [Theory]
    [InlineData(UserRole.Trainee, 403)]
    [InlineData(UserRole.OrganizationUser, 400)]
    public async Task Invalid_or_unauthorized_requests_do_not_read_store(UserRole role, int expectedStatus)
    {
        var actor = RevisionAccessTests.Actor(role);
        var store = StubProxy.For<IScenarioReadStore>((_, _) => throw new Exception("Unexpected store access"));
        var draftId = role == UserRole.Trainee ? Guid.NewGuid() : Guid.Empty;
        var result = await new ValidateScenarioDraftCommandHandler(RevisionAccessTests.Accounts(actor), store,
            StubProxy.For<Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog.IRuntimeCatalogReadStore>((_, _) => throw new Exception("Unexpected catalog access")))
            .Handle(new(actor.Id, draftId), default);
        Assert.Equal(expectedStatus, result.Error?.Status);
    }

    private const string ValidState = """
        {"spawnPoints":[{"x":1,"y":2,"z":3,"rotation":0}],"hazards":[],"scoringConfig":{"baseScore":100,"timeLimitSeconds":60,"penaltyPerMistake":5},"routingConfig":{"evacuationRoutes":["exit-a"]},"learningObjectives":["Evacuate"],"learnerInstructions":"Follow exits","rubric":{"schema_version":"1","pass_threshold":1,"criteria":[{"id":"exit","metric":"exit","mandatory":true,"weight":1,"operator":"gte","threshold":1}]}}
        """;

    private static IReadOnlyList<string> Codes(JsonNode state) => ScenarioDraftStructuralValidator.Validate(state).Select(x => x.Code + "@" + x.Path).ToList();

    [Fact]
    public void Structural_validator_checks_each_hazard()
    {
        var state = JsonNode.Parse(ValidState)!;
        state["hazards"] = JsonNode.Parse("""
            [null,{"id":" ","type":"","position":null,"intensity":-1,"activationTime":-0.5},{"id":"fire","type":"Fire","position":{"x":0,"y":0,"z":0,"rotation":0},"intensity":2,"activationTime":0}]
            """);
        var codes = Codes(state);
        Assert.Contains("HAZARD_REQUIRED@$.hazards[0]", codes);
        Assert.Contains("HAZARD_ID_REQUIRED@$.hazards[1].id", codes);
        Assert.Contains("HAZARD_TYPE_REQUIRED@$.hazards[1].type", codes);
        Assert.Contains("POSITION_REQUIRED@$.hazards[1].position", codes);
        Assert.Contains("HAZARD_INTENSITY_INVALID@$.hazards[1].intensity", codes);
        Assert.Contains("HAZARD_ACTIVATION_TIME_INVALID@$.hazards[1].activationTime", codes);
        Assert.DoesNotContain(codes, x => x.Contains("hazards[2]"));
    }

    [Fact]
    public void Structural_validator_reports_blank_routes_duplicate_anchors_capabilities_and_bad_rubric_criteria()
    {
        var state = JsonNode.Parse(ValidState)!;
        state["hazards"] = null;
        state["routingConfig"] = JsonNode.Parse("""{"evacuationRoutes":["exit-a"," "]}""");
        state["objectAnchors"] = JsonNode.Parse("""["door-1","door-1"]""");
        state["requiredCapabilities"] = JsonNode.Parse("""["smoke",""]""");
        state["rubric"]!["criteria"] = JsonNode.Parse("""
            [{"id":"exit","metric":"exit","mandatory":true,"weight":1,"operator":"gte","threshold":1},
             {"id":"exit","metric":"exit","mandatory":true,"weight":1,"operator":"gte","threshold":1},
             {"id":"time","metric":"time","mandatory":"yes","weight":-1,"operator":"between","threshold":1}]
            """);
        var codes = Codes(state);
        Assert.Contains("HAZARDS_REQUIRED@$.hazards", codes);
        Assert.Contains("ROUTE_INVALID@$.routingConfig.evacuationRoutes", codes);
        Assert.Contains("ANCHORS_INVALID@$.objectAnchors", codes);
        Assert.Contains("CAPABILITIES_INVALID@$.requiredCapabilities", codes);
        Assert.Contains("RUBRIC_CRITERION_INVALID@$.rubric.criteria[1]", codes);
        Assert.Contains("RUBRIC_CRITERION_INVALID@$.rubric.criteria[2]", codes);
        Assert.DoesNotContain("RUBRIC_CRITERION_INVALID@$.rubric.criteria[0]", codes);
    }

    [Fact]
    public void Structural_validator_reports_state_that_does_not_match_the_draft_schema()
    {
        var issue = Assert.Single(ScenarioDraftStructuralValidator.Validate(JsonNode.Parse("""{"spawnPoints":"north door"}""")!));
        Assert.Equal(("INVALID_STATE", "$"), (issue.Code, issue.Path));
    }

    private static ScenarioDraftResponse Draft(string state) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
        JsonNode.Parse(state)!, "Manual", null, DateTime.UtcNow, DateTime.UtcNow, 7);
    private static Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog.IRuntimeCatalogReadStore NoCatalog() =>
        StubProxy.For<Fire3D.Application.Scenarios.Queries.GetRuntimeCatalog.IRuntimeCatalogReadStore>((m, _) => throw new Exception("Unexpected catalog access: " + m));

    [Fact]
    public async Task Validate_returns_not_found_for_a_draft_outside_the_callers_organization()
    {
        var actor = RevisionAccessTests.Actor(UserRole.OrganizationUser);
        Guid? scope = null;
        var store = StubProxy.For<IScenarioReadStore>((method, args) => method == nameof(IScenarioReadStore.GetScenarioDraftAsync)
            ? Task.FromResult<ScenarioDraftResponse?>((scope = (Guid?)args[1]) is null ? Draft("{}") : null) : throw new Exception("Unexpected: " + method));
        var result = await new ValidateScenarioDraftCommandHandler(RevisionAccessTests.Accounts(actor), store, NoCatalog()).Handle(new(actor.Id, Guid.NewGuid()), default);
        Assert.Equal(("NOT_FOUND", 404), (result.Error?.Code, result.Error?.Status));
        Assert.Equal(actor.OrganizationId, scope);
    }

    [Fact]
    public async Task Validate_reports_an_unsupported_editor_schema_version_as_the_only_issue()
    {
        var actor = RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var draft = Draft("""{"schemaVersion":"fet3d.editor/99","spawnPoints":[]}""");
        var store = StubProxy.For<IScenarioReadStore>((method, _) => method == nameof(IScenarioReadStore.GetScenarioDraftAsync)
            ? Task.FromResult<ScenarioDraftResponse?>(draft) : throw new Exception("Unexpected: " + method));
        var result = await new ValidateScenarioDraftCommandHandler(RevisionAccessTests.Accounts(actor), store, NoCatalog()).Handle(new(actor.Id, draft.Id), default);
        Assert.True(result.IsSuccess, result.Error?.Code);
        Assert.False(result.Value!.IsValid);
        Assert.Equal(7u, result.Value.Version);
        var issue = Assert.Single(result.Value.Issues);
        Assert.Equal(("EDITOR_SCHEMA_VERSION_UNSUPPORTED", "$.schemaVersion"), (issue.Code, issue.Path));
    }

    [Fact]
    public async Task Dry_run_requires_a_draft_id_before_reading_the_store()
    {
        var actor = RevisionAccessTests.Actor(UserRole.OrganizationUser);
        var result = await new ValidateScenarioDraftStateCommandHandler(RevisionAccessTests.Accounts(actor),
                StubProxy.For<IScenarioReadStore>((m, _) => throw new Exception("Unexpected store access: " + m)), NoCatalog())
            .Handle(new(actor.Id, Guid.Empty, JsonNode.Parse(ValidState)), default);
        Assert.Equal(("VALIDATION_ERROR", 400, "Scenario draft ID is required."), (result.Error?.Code, result.Error?.Status, result.Error?.Message));
    }
}
