using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Buildings;
using Fire3D.Application.Buildings.Queries.ListRevisions;
using Fire3D.Application.Ifc;
using Fire3D.Application.Ifc.Commands.ConfirmForTraining;
using Fire3D.Application.Releases;
using Fire3D.Application.Scenarios;
using Fire3D.Application.Scenarios.Commands.CreateScenario;
using Fire3D.Application.Scenarios.Commands.CreateScenarioDraft;
using Fire3D.Application.Scenarios.Queries.ListScenarioDrafts;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.IfcTests;

/// <summary>Input guards of IFC, scenario and release handlers: the request is refused before any store is touched.</summary>
public sealed class HandlerGuardTests
{
    private static T Untouched<T>() where T : class => StubProxy.For<T>((method, _) => throw new Exception("Store must not be read: " + method));
    private static Domain.Entities.User Org() => RevisionAccessTests.Actor(UserRole.OrganizationUser);

    [Fact]
    public async Task Confirm_for_training_requires_exact_version_and_run_and_rejects_an_empty_annotation_id()
    {
        var actor = Org();
        var handler = new ConfirmForTrainingHandler(RevisionAccessTests.Accounts(actor), Untouched<IScenarioReadinessStore>());
        foreach (var request in new ConfirmTrainingRequest?[] { null, new(Guid.Empty, Guid.NewGuid()), new(Guid.NewGuid(), Guid.Empty), new(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty) })
        {
            var result = await handler.Handle(new(actor.Id, Guid.NewGuid(), Guid.NewGuid(), request), default);
            Assert.Equal(("VALIDATION_ERROR", 400), (result.Error?.Code, result.Error?.Status));
            Assert.True(result.Error!.Errors!.ContainsKey("scenarioVersionId"));
        }
    }

    [Fact]
    public async Task Confirm_for_training_runs_the_Confirm_gate_for_the_session_family_and_returns_the_review_id()
    {
        var actor = Org(); var family = Guid.NewGuid(); var revision = Guid.NewGuid(); var review = Guid.NewGuid();
        var request = new ConfirmTrainingRequest(Guid.NewGuid(), Guid.NewGuid());
        object?[]? call = null;
        var store = StubProxy.For<IScenarioReadinessStore>((_, args) =>
        {
            call = args;
            return Task.FromResult(AuthResult<JsonElement>.Ok(JsonSerializer.SerializeToElement(new { code = "OK", reviewId = review })));
        });
        var result = await new ConfirmForTrainingHandler(RevisionAccessTests.Accounts(actor), store).Handle(new(actor.Id, family, revision, request), default);
        Assert.Equal(review, result.Value);
        Assert.Equal(new object?[] { "Confirm", actor.Id, family, request.ScenarioVersionId, revision, request, null }, call![..7]);

        var refused = StubProxy.For<IScenarioReadinessStore>((_, _) => Task.FromResult(AuthResult<JsonElement>.Fail("READINESS_BLOCKED", "Blocked.", 409)));
        var blocked = await new ConfirmForTrainingHandler(RevisionAccessTests.Accounts(actor), refused).Handle(new(actor.Id, family, revision, request), default);
        Assert.Equal(("READINESS_BLOCKED", 409), (blocked.Error?.Code, blocked.Error?.Status));
    }

    [Fact]
    public async Task Unknown_or_inactive_actor_is_unauthorized_before_reading_IFC_or_revisions()
    {
        Assert.Equal(401, (await new GetProcessingJobHandler(RevisionAccessTests.Accounts(null), Untouched<IIfcReadStore>()).Handle(new(Guid.NewGuid(), Guid.NewGuid()), default)).Error?.Status);
        var inactive = Org(); inactive.IsActive = false;
        Assert.Equal(401, (await new ListRevisionsQueryHandler(Untouched<IBuildingStore>(), RevisionAccessTests.Accounts(inactive)).Handle(new(inactive.Id, Guid.NewGuid()), default)).Error?.Status);
        Assert.Equal(401, (await new ListRevisionsQueryHandler(Untouched<IBuildingStore>(), RevisionAccessTests.Accounts(null)).Handle(new(Guid.NewGuid(), Guid.NewGuid()), default)).Error?.Status);
    }

    [Fact]
    public async Task Empty_identifiers_are_validation_errors_without_store_access()
    {
        var actor = Org(); var accounts = RevisionAccessTests.Accounts(actor);
        var results = new (string Name, AuthError? Error)[]
        {
            ("job", (await new GetProcessingJobHandler(accounts, Untouched<IIfcReadStore>()).Handle(new(actor.Id, Guid.Empty), default)).Error),
            ("validation run", (await new GetValidationRunHandler(accounts, Untouched<IIfcReadStore>()).Handle(new(actor.Id, Guid.Empty), default)).Error),
            ("release", (await new GetReleaseHandler(accounts, Untouched<IReleaseStore>()).Handle(new(actor.Id, Guid.Empty), default)).Error),
            ("preview", (await new GetEditorPreviewHandler(accounts, Untouched<IEditorPreviewStore>(), Untouched<IPreviewDownloadSigner>()).Handle(new(actor.Id, Guid.NewGuid(), Guid.Empty), default)).Error),
            ("floors", (await new GetRevisionFloorsHandler(accounts, Untouched<IEditorPreviewStore>()).Handle(new(actor.Id, Guid.Empty), default)).Error),
            ("scenario", (await new CreateScenarioHandler(accounts, Untouched<IScenarioWriteStore>()).Handle(new(actor.Id, new(Guid.NewGuid(), " ")), default)).Error),
            ("draft", (await new CreateScenarioDraftHandler(accounts, Untouched<IScenarioWriteStore>()).Handle(new(actor.Id, Guid.NewGuid(), new(Guid.Empty)), default)).Error),
            ("drafts page", (await new ListScenarioDraftsQueryHandler(accounts, Untouched<IScenarioReadStore>()).Handle(new(actor.Id, Guid.NewGuid(), 1, 101), default)).Error),
            ("annotations", (await new SaveAnnotationsHandler(accounts, Untouched<IAnnotationStore>()).Handle(new(actor.Id, Guid.NewGuid(), "\"3\"",
                new([new(Guid.NewGuid(), "2O2Fr$t4X7Zf8NOew3FLOH", "Exit", null), new(Guid.NewGuid(), "2O2Fr$t4X7Zf8NOew3FLOH", " ", null)])), default)).Error),
            ("release thresholds", (await new BuildReleaseHandler(accounts, Untouched<IReleaseStore>()).Handle(new(actor.Id,
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), SafetyThresholds: "{not json")), default)).Error),
        };
        Assert.All(results, x => Assert.True(x.Error is { Code: "VALIDATION_ERROR", Status: 400 }, x.Name));
    }
}
