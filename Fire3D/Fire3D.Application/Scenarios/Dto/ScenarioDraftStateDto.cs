using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace Fire3D.Application.Scenarios.Dto;

public record SpawnPoint(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("rotation")] double Rotation
);

public record Hazard(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("position")] SpawnPoint Position,
    [property: JsonPropertyName("intensity")] double Intensity,
    [property: JsonPropertyName("activationTime")] double ActivationTime
);

public record ScoringConfig(
    [property: JsonPropertyName("baseScore")] int BaseScore,
    [property: JsonPropertyName("timeLimitSeconds")] int TimeLimitSeconds,
    [property: JsonPropertyName("penaltyPerMistake")] int PenaltyPerMistake
);

public record RoutingConfig(
    [property: JsonPropertyName("evacuationRoutes")] List<string> EvacuationRoutes
);

public record ScenarioDraftStateDto(
    [property: JsonPropertyName("spawnPoints")] List<SpawnPoint> SpawnPoints,
    [property: JsonPropertyName("hazards")] List<Hazard> Hazards,
    [property: JsonPropertyName("scoringConfig")] ScoringConfig ScoringConfig,
    [property: JsonPropertyName("routingConfig")] RoutingConfig RoutingConfig,
    [property: JsonPropertyName("rubric")] JsonObject? Rubric = null,
    [property: JsonPropertyName("learningObjectives")] List<string>? LearningObjectives = null,
    [property: JsonPropertyName("learnerInstructions")] string? LearnerInstructions = null,
    [property: JsonPropertyName("objectAnchors")] List<string>? ObjectAnchors = null,
    [property: JsonPropertyName("requiredCapabilities")] List<string>? RequiredCapabilities = null,
    [property: JsonPropertyName("runtimeVersion")] string? RuntimeVersion = null,
    [property: JsonPropertyName("goals")] JsonArray? Goals = null,
    [property: JsonPropertyName("npcs")] JsonArray? Npcs = null,
    [property: JsonPropertyName("blockedElements")] JsonArray? BlockedElements = null,
    [property: JsonPropertyName("modePolicy")] JsonObject? ModePolicy = null,
    [property: JsonPropertyName("safetyThresholds")] JsonObject? SafetyThresholds = null,
    [property: JsonPropertyName("randomSeed")] long RandomSeed = 0,
    [property: JsonPropertyName("replanIntervalSeconds")] int ReplanIntervalSeconds = 0
);
