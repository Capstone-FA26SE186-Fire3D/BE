using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Authentication;
using Fire3D.Application.Editor;
using Fire3D.Application.Ifc;
using Fire3D.Application.Scenarios.Dto;
using MediatR;

namespace Fire3D.Application.Scenarios.Commands.UpdateScenarioDraft;

/// <summary>State is the raw request body so a versioned draft round-trips every supported field.</summary>
public sealed record UpdateScenarioDraftCommand(Guid ActorId, Guid DraftId, uint ExpectedVersion, JsonNode? State) : IRequest<AuthResult<uint>>;

public sealed class UpdateScenarioDraftHandler(IAuthStore accounts, IScenarioWriteStore store)
    : IRequestHandler<UpdateScenarioDraftCommand, AuthResult<uint>>
{
    public async Task<AuthResult<uint>> Handle(UpdateScenarioDraftCommand command, CancellationToken ct)
    {
        var scope = await IfcAccess.ResolveAsync(accounts, command.ActorId, ct);
        if (!scope.IsSuccess) return new(default, scope.Error);

        var state = DraftStateInput.Normalize(command.State);
        if (!state.IsSuccess) return new(default, state.Error);
        return await store.UpdateScenarioDraftAsync(command.ActorId, command.DraftId, command.ExpectedVersion, state.Value!, scope.Value!.OrganizationId, ct);
    }
}

public static class DraftStateInput
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Versioned drafts must pass shape validation and are stored as sent. Legacy drafts (no schemaVersion) keep the
    /// historical typed projection; a malformed or unsupported schemaVersion is rejected rather than silently migrated.
    /// </summary>
    public static AuthResult<JsonNode> Normalize(JsonNode? state)
    {
        if (state is not JsonObject)
            return AuthResult<JsonNode>.Fail("VALIDATION_ERROR", "Draft state must be a JSON object.", 400);
        var version = EditorContract.DeclaredVersion(state);
        if (version is null)
        {
            ScenarioDraftStateDto? legacy;
            try { legacy = state.Deserialize<ScenarioDraftStateDto>(Web); }
            catch (JsonException) { legacy = null; }
            return legacy is null
                ? AuthResult<JsonNode>.Fail("VALIDATION_ERROR", "Draft state does not match the legacy draft shape.", 400)
                : AuthResult<JsonNode>.Ok(JsonSerializer.SerializeToNode(legacy, Web)!);
        }
        if (!EditorContract.IsSupported(version))
            return Unsupported<JsonNode>();
        var issues = ScenarioStateV1Validator.ValidateShape(state);
        return issues.Count == 0 ? AuthResult<JsonNode>.Ok(state) : Invalid<JsonNode>(issues);
    }

    public static AuthResult<T> Unsupported<T>() => AuthResult<T>.Fail("EDITOR_SCHEMA_VERSION_UNSUPPORTED",
        $"schemaVersion is not supported. Supported: {string.Join(", ", EditorContract.SupportedVersions)}.", 422);

    public static AuthResult<T> Invalid<T>(IReadOnlyList<Fire3D.Application.Scenarios.Commands.ValidateScenarioDraft.ScenarioDraftValidationIssue> issues) =>
        new(default, new AuthError("EDITOR_SCHEMA_INVALID", "Editor document does not satisfy its schema version.", 422, Issues: issues));
}
