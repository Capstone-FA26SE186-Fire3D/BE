using System.Text.Json;
using Fire3D.Application.Authentication;
namespace Fire3D.Application.Buildings;
public sealed record BuildingAccessRequest(string Visibility);
public sealed record ParticipationCodeRequest(string Code);
public interface IBuildingAccessService
{
 Task<AuthResult<JsonElement>> Execute(string action,Guid actor,Guid family,Guid building,object input,long? expected,CancellationToken ct);
}

public sealed record BuildingAccessResponse(Guid BuildingId,string Visibility,long AccessRevision,bool HasParticipationCode,string? Code=null);
