using System.Text.Json;
using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using Fire3D.Application.Scenarios;
using Fire3D.Infrastructure.Persistence;

namespace Fire3D.Infrastructure.Scenarios;

public sealed class ScenarioReviewQueries(Fire3DDbContext db) : IScenarioReviewQueries
{
    public async Task<AuthResult<PageResponse<ScenarioReviewSummary>>> ListAsync(Guid actor,Guid? family,string? status,Guid? organizationId,int page,int pageSize,CancellationToken ct)
    {
        var errors=new Dictionary<string,string[]>();
        if(status is not null && status is not ("Submitted" or "Approved" or "Rejected")) errors["status"]=["Use Submitted, Approved or Rejected."];
        if(page<1) errors["page"]=["Page must be at least 1."];
        if(pageSize is <1 or >100) errors["pageSize"]=["Page size must be 1–100."];
        if(organizationId==Guid.Empty) errors["organizationId"]=["Use a non-empty organization UUID."];
        if(errors.Count>0) return AuthResult<PageResponse<ScenarioReviewSummary>>.Fail("VALIDATION_ERROR","Review filters are invalid.",400,errors);
        var result=await JsonCommandGate.Execute(db,"scenario_review_read_gate","List",actor,family,null,new {status,organizationId,page,pageSize},null,null,ct);
        return result.IsSuccess?AuthResult<PageResponse<ScenarioReviewSummary>>.Ok(result.Value.Deserialize<PageResponse<ScenarioReviewSummary>>(JsonCommandGate.Json)!):new(default,result.Error);
    }
    public async Task<AuthResult<ScenarioReviewDetail>> DetailAsync(Guid actor,Guid? family,Guid id,bool byVersion,CancellationToken ct)
    {
        if(id==Guid.Empty) return AuthResult<ScenarioReviewDetail>.Fail("VALIDATION_ERROR","Review/version id is required.",400);
        var result=await JsonCommandGate.Execute(db,"scenario_review_read_gate",byVersion?"Version":"Detail",actor,family,id,new{},null,null,ct);
        return result.IsSuccess?AuthResult<ScenarioReviewDetail>.Ok(result.Value.Deserialize<ScenarioReviewDetail>(JsonCommandGate.Json)!):new(default,result.Error);
    }
    public async Task<AuthResult<IReadOnlyList<ScenarioVersionReviewState>>> StatesAsync(Guid actor,Guid? family,IReadOnlyList<Guid> ids,CancellationToken ct)
    {
        if(ids.Count>100 || ids.Any(id=>id==Guid.Empty)) return AuthResult<IReadOnlyList<ScenarioVersionReviewState>>.Fail("VALIDATION_ERROR","At most 100 non-empty version ids are allowed.",400);
        var result=await JsonCommandGate.Execute(db,"scenario_review_read_gate","States",actor,family,null,new{ids},null,null,ct);
        return result.IsSuccess?AuthResult<IReadOnlyList<ScenarioVersionReviewState>>.Ok(result.Value.Deserialize<List<ScenarioVersionReviewState>>(JsonCommandGate.Json)!):new(default,result.Error);
    }
    public Task<AuthResult<ScenarioVersionReadiness>> VersionReadinessAsync(Guid actor,Guid? family,Guid versionId,CancellationToken ct)=>Readiness<ScenarioVersionReadiness>("Version",actor,family,versionId,ct);
    public Task<AuthResult<RevisionReadiness>> RevisionReadinessAsync(Guid actor,Guid? family,Guid revisionId,CancellationToken ct)=>Readiness<RevisionReadiness>("Revision",actor,family,revisionId,ct);
    private async Task<AuthResult<T>> Readiness<T>(string action,Guid actor,Guid? family,Guid id,CancellationToken ct)
    {
        if(id==Guid.Empty) return AuthResult<T>.Fail("VALIDATION_ERROR","A non-empty id is required.",400);
        var result=await JsonCommandGate.Execute(db,"authoring_readiness_gate",action,actor,family,id,new{},null,null,ct);
        return result.IsSuccess?AuthResult<T>.Ok(result.Value.Deserialize<T>(JsonCommandGate.Json)!):new(default,result.Error);
    }
}
