using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Buildings.Queries.GetTrainings;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace Fire3D.Infrastructure.Buildings;
public sealed class TrainingReadStore(Fire3DDbContext db):ITrainingReadStore
{
 public async Task<AuthResult<List<TrainingDto>>> GetTrainingsByBuildingAsync(Guid actorId,Guid buildingId,Guid? organizationId,CancellationToken ct,Guid? family=null)
 {
  var result=await JsonCommandGate.Execute(db,"release_access_gate","List",actorId,family,buildingId,new{},null,null,ct);
  var json=new JsonSerializerOptions(JsonCommandGate.Json);json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
  if(!result.IsSuccess)return new(default,result.Error);
  var items=result.Value.Deserialize<List<TrainingDto>>(json)!;
  // The gate lists only Trainings pinned to an Approved content review; add the release state and version it serves.
  var ids=items.Select(x=>x.ReleaseId).Distinct().ToList();
  var releases=(await db.Database.SqlQuery<ReleaseState>($"SELECT id AS \"Id\",status::text AS \"Status\",scenario_version_id AS \"ScenarioVersionId\" FROM releases WHERE id=ANY({ids.ToArray()})").ToListAsync(ct)).ToDictionary(x=>x.Id);
  return AuthResult<List<TrainingDto>>.Ok(items.Select(x=>releases.TryGetValue(x.ReleaseId,out var r)?x with{ReleaseStatus=r.Status,ScenarioVersionId=r.ScenarioVersionId}:x).ToList());
 }
 private sealed record ReleaseState(Guid Id,string Status,Guid ScenarioVersionId);
}
