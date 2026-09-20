using System.Text.Json.Serialization;

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
    [property: JsonPropertyName("routingConfig")] RoutingConfig RoutingConfig
);
