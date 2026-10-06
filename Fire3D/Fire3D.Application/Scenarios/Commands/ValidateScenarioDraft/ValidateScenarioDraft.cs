using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using Fire3D.Application.Scenarios.Dto;
using MediatR;

namespace Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft;

public sealed record ScenarioDraftValidationIssue(string Code, string Path, string Message);
public sealed record ScenarioDraftValidationResponse(Guid DraftId, uint Version, bool IsValid,
    IReadOnlyList<ScenarioDraftValidationIssue> Issues);
public sealed record ValidateScenarioDraftCommand(Guid ActorId, Guid DraftId)
    : IRequest<AuthResult<ScenarioDraftValidationResponse>>;

public sealed class ValidateScenarioDraftCommandHandler(IAuthStore accounts, IScenarioReadStore store)
    : IRequestHandler<ValidateScenarioDraftCommand, AuthResult<ScenarioDraftValidationResponse>>
{
    public async Task<AuthResult<ScenarioDraftValidationResponse>> Handle(ValidateScenarioDraftCommand request, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, request.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);
        if (request.DraftId == Guid.Empty)
            return AuthResult<ScenarioDraftValidationResponse>.Fail("VALIDATION_ERROR", "Scenario draft ID is required.", 400);

        var draft = await store.GetScenarioDraftAsync(request.DraftId, scope.Value!.OrganizationId, ct);
        if (draft is null)
            return AuthResult<ScenarioDraftValidationResponse>.Fail("NOT_FOUND", "Scenario draft not found or access denied.", 404);

        var issues = ScenarioDraftStructuralValidator.Validate(draft.State).Concat(await store.ValidateReferencesAsync(draft.RevisionId,draft.State,ct)).ToList();
        return AuthResult<ScenarioDraftValidationResponse>.Ok(
            new ScenarioDraftValidationResponse(draft.Id, draft.Version, issues.Count == 0, issues));
    }
}

public static class ScenarioDraftStructuralValidator
{
    public static IReadOnlyList<ScenarioDraftValidationIssue> Validate(System.Text.Json.Nodes.JsonNode state)
    {
        ScenarioDraftStateDto? draft;
        try { draft = state.Deserialize<ScenarioDraftStateDto>(); }
        catch (JsonException) { return [new("INVALID_STATE", "$", "Draft state must match the scenario draft schema.")]; }
        if (draft is null) return [new("INVALID_STATE", "$", "Draft state must be a JSON object.")];

        var issues = new List<ScenarioDraftValidationIssue>();
        if (draft.SpawnPoints is not { Count: > 0 })
            issues.Add(new("SPAWN_REQUIRED", "$.spawnPoints", "At least one spawn point is required."));
        else for (var index = 0; index < draft.SpawnPoints.Count; index++)
            ValidatePosition(draft.SpawnPoints[index], $"$.spawnPoints[{index}]", issues);

        if (draft.Hazards is null) issues.Add(new("HAZARDS_REQUIRED", "$.hazards", "Hazards must be an array."));
        else for (var index = 0; index < draft.Hazards.Count; index++)
        {
            var hazard = draft.Hazards[index];
            var path = $"$.hazards[{index}]";
            if(hazard is null){issues.Add(new("HAZARD_REQUIRED",path,"Hazard cannot be null."));continue;}
            if (string.IsNullOrWhiteSpace(hazard.Id)) issues.Add(new("HAZARD_ID_REQUIRED", $"{path}.id", "Hazard ID is required."));
            if (string.IsNullOrWhiteSpace(hazard.Type)) issues.Add(new("HAZARD_TYPE_REQUIRED", $"{path}.type", "Hazard type is required."));
            ValidatePosition(hazard.Position, $"{path}.position", issues);
            if (!double.IsFinite(hazard.Intensity) || hazard.Intensity < 0)
                issues.Add(new("HAZARD_INTENSITY_INVALID", $"{path}.intensity", "Hazard intensity must be a finite non-negative number."));
            if (!double.IsFinite(hazard.ActivationTime) || hazard.ActivationTime < 0)
                issues.Add(new("HAZARD_ACTIVATION_TIME_INVALID", $"{path}.activationTime", "Activation time must be a finite non-negative number."));
        }

        if (draft.ScoringConfig is null) issues.Add(new("SCORING_REQUIRED", "$.scoringConfig", "Scoring configuration is required."));
        else
        {
            if (draft.ScoringConfig.BaseScore < 0) issues.Add(new("BASE_SCORE_INVALID", "$.scoringConfig.baseScore", "Base score cannot be negative."));
            if (draft.ScoringConfig.TimeLimitSeconds <= 0) issues.Add(new("TIME_LIMIT_INVALID", "$.scoringConfig.timeLimitSeconds", "Time limit must be greater than zero."));
            if (draft.ScoringConfig.PenaltyPerMistake < 0) issues.Add(new("PENALTY_INVALID", "$.scoringConfig.penaltyPerMistake", "Penalty per mistake cannot be negative."));
        }

        if (draft.RoutingConfig?.EvacuationRoutes is not { Count: > 0 })
            issues.Add(new("ROUTE_REQUIRED", "$.routingConfig.evacuationRoutes", "At least one evacuation route is required."));
        else if (draft.RoutingConfig.EvacuationRoutes.Any(string.IsNullOrWhiteSpace))
            issues.Add(new("ROUTE_INVALID", "$.routingConfig.evacuationRoutes", "Evacuation routes cannot contain blank values."));
        if(draft.LearningObjectives is not {Count:>0} || draft.LearningObjectives.Any(x=>string.IsNullOrWhiteSpace(x)||x.Length>1000))
            issues.Add(new("LEARNING_OBJECTIVES_REQUIRED","$.learningObjectives","Provide nonblank learner objectives, each at most 1000 characters."));
        if(string.IsNullOrWhiteSpace(draft.LearnerInstructions)||draft.LearnerInstructions.Length>10000)
            issues.Add(new("LEARNER_INSTRUCTIONS_REQUIRED","$.learnerInstructions","Provide learner instructions of 1-10000 characters."));
        ValidateRubric(draft.Rubric,issues);
        if(draft.ObjectAnchors?.Any(string.IsNullOrWhiteSpace)==true || draft.ObjectAnchors?.Distinct().Count()!=draft.ObjectAnchors?.Count)
            issues.Add(new("ANCHORS_INVALID","$.objectAnchors","Anchor IDs must be nonblank and unique."));
        if(draft.RequiredCapabilities?.Any(string.IsNullOrWhiteSpace)==true || draft.RequiredCapabilities?.Distinct().Count()!=draft.RequiredCapabilities?.Count)
            issues.Add(new("CAPABILITIES_INVALID","$.requiredCapabilities","Capabilities must be nonblank and unique."));
        return issues;
    }

    private static void ValidateRubric(System.Text.Json.Nodes.JsonObject? rubric,List<ScenarioDraftValidationIssue> issues)
    {
        bool Text(System.Text.Json.Nodes.JsonNode? n)=>n is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text);
        bool Number(System.Text.Json.Nodes.JsonNode? n,out double number){number=0;return n is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<double>(out number)&&double.IsFinite(number);}
        if(rubric is null || !Text(rubric["schema_version"]) || !Number(rubric["pass_threshold"],out _) || rubric["criteria"] is not System.Text.Json.Nodes.JsonArray {Count:>0} criteria)
        {issues.Add(new("RUBRIC_REQUIRED","$.rubric","Provide schema_version, numeric pass_threshold and nonempty criteria."));return;}
        var ids=new HashSet<string>(StringComparer.Ordinal);
        for(var i=0;i<criteria.Count;i++)
        {
            var c=criteria[i] as System.Text.Json.Nodes.JsonObject;
            if(c is null || !Text(c["id"]) || !Text(c["metric"]) || c["mandatory"] is not System.Text.Json.Nodes.JsonValue mandatory || !mandatory.TryGetValue<bool>(out _) || !Number(c["weight"],out var weight) || weight<0 || !Number(c["threshold"],out _) || !Text(c["operator"]) || c["operator"]!.GetValue<string>() is not ("gte" or "lte" or "eq") || !ids.Add(c["id"]!.GetValue<string>()))
                issues.Add(new("RUBRIC_CRITERION_INVALID",$"$.rubric.criteria[{i}]","Each criterion needs a unique ID, metric, mandatory flag, nonnegative weight, gte/lte/eq and numeric threshold."));
        }
    }

    private static void ValidatePosition(SpawnPoint? point, string path, ICollection<ScenarioDraftValidationIssue> issues)
    {
        if (point is null) { issues.Add(new("POSITION_REQUIRED", path, "Position is required.")); return; }
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z) || !double.IsFinite(point.Rotation))
            issues.Add(new("POSITION_INVALID", path, "Position coordinates and rotation must be finite numbers."));
    }
}
