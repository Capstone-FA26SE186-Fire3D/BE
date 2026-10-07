using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Buildings;
using Fire3D.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;
namespace Fire3D.Infrastructure.Buildings;
public sealed class BuildingAccessService(Fire3DDbContext db):IBuildingAccessService
{
 public async Task<AuthResult<JsonElement>> Execute(string action,Guid actor,Guid family,Guid building,object input,long? expected,CancellationToken ct)
 {
  string? code=null;
  if(action=="Rotate"){code=WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));input=new{codeHash=Hash(code)};}
  if(action=="Verify")
  {
   var value=(ParticipationCodeRequest)input;
   if(value.Code is null || value.Code.Length is <8 or >128 || value.Code.Any(c=>char.IsWhiteSpace(c)||char.IsControl(c)))return AuthResult<JsonElement>.Fail("VALIDATION_ERROR","Use the current participation code.",400,new Dictionary<string,string[]>{["code"]=["Code must be 8-128 characters without whitespace."]});
   input=new{codeHash=Hash(value.Code)};
  }
  if(action=="Access" && ((BuildingAccessRequest)input).Visibility is not ("Public" or "Private"))return AuthResult<JsonElement>.Fail("VALIDATION_ERROR","Choose Public or Private.",400,new Dictionary<string,string[]>{["visibility"]=["Choose Public or Private."]});
  var result=await JsonCommandGate.Execute(db,"release_access_gate",action,actor,family,building,input,null,expected,ct);
  if(result.IsSuccess && code is not null){var node=System.Text.Json.Nodes.JsonNode.Parse(result.Value.GetRawText())!;node["code"]=code;return AuthResult<JsonElement>.Ok(JsonSerializer.SerializeToElement(node));}
  return result;
 }
 private static string Hash(string value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
