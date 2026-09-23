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
            {"spawnPoints":[{"x":1,"y":2,"z":3,"rotation":0}],"hazards":[],"scoringConfig":{"baseScore":100,"timeLimitSeconds":60,"penaltyPerMistake":5},"routingConfig":{"evacuationRoutes":["exit-a"]}}
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
        var result = await new ValidateScenarioDraftCommandHandler(RevisionAccessTests.Accounts(actor), store)
            .Handle(new(actor.Id, draftId), default);
        Assert.Equal(expectedStatus, result.Error?.Status);
    }
}
