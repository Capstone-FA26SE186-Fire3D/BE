using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios.Commands.RejectScenarioVersion;
using Fire3D.Infrastructure.Persistence;
namespace Fire3D.Infrastructure.Scenarios;
public sealed class ScenarioReviewStore(Fire3DDbContext db):IScenarioReviewStore
{
    public async Task<AuthResult<Guid>> CreateRejectedReviewAsync(Guid actorId,Guid familyId,Guid revisionId,Guid? organizationId,RejectScenarioVersionRequest request,CancellationToken ct)
    {
        var result=await new ScenarioReadinessStore(db).ExecuteAsync("TechnicalReject",actorId,familyId,request.ScenarioVersionId,revisionId,request,null,ct);
        return result.IsSuccess?AuthResult<Guid>.Ok(result.Value.GetProperty("reviewId").GetGuid()):new(default,result.Error);
    }
}
