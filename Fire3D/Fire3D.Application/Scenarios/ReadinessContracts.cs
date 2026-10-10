using Fire3D.Application.Authentication;
using System.Text.Json;
namespace Fire3D.Application.Scenarios;
public sealed record ContentReviewResponse(string Code, Guid ReviewId, string Status, string ContentHash, string RubricHash);
public sealed record ConfirmationResponse(Guid ReviewId);
public sealed record TechnicalRejectionResponse(Guid Id);
public sealed record ConfirmTrainingRequest(Guid ScenarioVersionId,Guid ValidationRunId,Guid? AnnotationSetId=null);
public sealed record ContentReviewDecisionRequest(string ContentHash,string RubricHash,string? Reason=null);
public interface IScenarioReadinessStore
{
    Task<AuthResult<JsonElement>> ExecuteAsync(string action,Guid actor,Guid family,Guid version,Guid? revision,object input,string? key,CancellationToken ct);
}
