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

        var issues = ScenarioDraftStructuralValidator.Validate(draft.State);
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
        return issues;
    }

    private static void ValidatePosition(SpawnPoint? point, string path, ICollection<ScenarioDraftValidationIssue> issues)
    {
        if (point is null) { issues.Add(new("POSITION_REQUIRED", path, "Position is required.")); return; }
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z) || !double.IsFinite(point.Rotation))
            issues.Add(new("POSITION_INVALID", path, "Position coordinates and rotation must be finite numbers."));
    }
}
