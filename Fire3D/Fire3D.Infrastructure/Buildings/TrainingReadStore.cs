using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Buildings.Queries.GetTrainings;
using Fire3D.Infrastructure.Persistence;
namespace Fire3D.Infrastructure.Buildings;
public sealed class TrainingReadStore(Fire3DDbContext db):ITrainingReadStore
{
 public async Task<AuthResult<List<TrainingDto>>> GetTrainingsByBuildingAsync(Guid actorId,Guid buildingId,Guid? organizationId,CancellationToken ct,Guid? family=null)
 {
  var result=await JsonCommandGate.Execute(db,"release_access_gate","List",actorId,family,buildingId,new{},null,null,ct);
  var json=new JsonSerializerOptions(JsonCommandGate.Json);json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
  return result.IsSuccess?AuthResult<List<TrainingDto>>.Ok(result.Value.Deserialize<List<TrainingDto>>(json)!):new(default,result.Error);
 }
}
