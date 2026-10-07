using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios;
using MediatR;
namespace Fire3D.Application.Ifc.Commands.ConfirmForTraining;
public sealed record ConfirmForTrainingCommand(Guid ActorId,Guid RevisionId,ConfirmTrainingRequest? Request=null):IRequest<AuthResult<Guid>>;
public sealed class ConfirmForTrainingHandler(IAuthStore accounts,IScenarioReadinessStore store):IRequestHandler<ConfirmForTrainingCommand,AuthResult<Guid>>
{
    public async Task<AuthResult<Guid>> Handle(ConfirmForTrainingCommand command,CancellationToken ct)
    {
        var scope=await IfcAccess.ResolveAsync(accounts,command.ActorId,ct);if(!scope.IsSuccess)return new(default,scope.Error);
        if(command.Request is null || command.Request.ScenarioVersionId==Guid.Empty || command.Request.ValidationRunId==Guid.Empty || command.Request.AnnotationSetId==Guid.Empty)
            return AuthResult<Guid>.Fail("VALIDATION_ERROR","Provide a scenarioVersionId and validationRunId; annotationSetId must match the validated annotation or be null.",400,new Dictionary<string,string[]>{["scenarioVersionId"]=["Exact version and validation run are required."]});
        var result=await store.ExecuteAsync("Confirm",command.ActorId,command.Request.ScenarioVersionId,command.RevisionId,command.Request,null,ct);
        return result.IsSuccess?AuthResult<Guid>.Ok(result.Value.GetProperty("reviewId").GetGuid()):new(default,result.Error);
    }
}
