using System.Text.Json;
using Fire3D.API.Authorization;
using Fire3D.Application.Authentication;
using Fire3D.Application.Buildings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Fire3D.API.Controllers;
[ApiController][Authorize][ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class BuildingAccessController(IBuildingAccessService service):ControllerBase
{
 [ProducesResponseType(typeof(BuildingAccessResponse),200)][HttpGet("api/buildings/{id:guid}/access")][Authorize(Roles="OrganizationUser,PlatformAdmin")]
 public Task<IActionResult> Get(Guid id,CancellationToken ct)=>Run("GetAccess",id,new{},null,ct);
 [ProducesResponseType(typeof(BuildingAccessResponse),200)][HttpPatch("api/buildings/{id:guid}/access")][Authorize(Roles="OrganizationUser,PlatformAdmin")]
 public Task<IActionResult> Update(Guid id,BuildingAccessRequest request,[FromHeader(Name="If-Match")]string? match,CancellationToken ct)=>Run("Access",id,request,match,ct);
 [ProducesResponseType(typeof(BuildingAccessResponse),200)][HttpPost("api/buildings/{id:guid}/participation-code/rotate")][Authorize(Roles="OrganizationUser,PlatformAdmin")]
 public Task<IActionResult> Rotate(Guid id,[FromHeader(Name="If-Match")]string? match,CancellationToken ct)=>Run("Rotate",id,new{},match,ct);
 [ProducesResponseType(typeof(BuildingAccessResponse),200)][HttpDelete("api/buildings/{id:guid}/participation-code")][Authorize(Roles="OrganizationUser,PlatformAdmin")]
 public Task<IActionResult> Revoke(Guid id,[FromHeader(Name="If-Match")]string? match,CancellationToken ct)=>Run("RevokeCode",id,new{},match,ct);
 [ProducesResponseType(typeof(BuildingAccessResponse),200)][HttpPost("api/buildings/{id:guid}/participation/verify")][Authorize(Roles="Trainee")]
 public Task<IActionResult> Verify(Guid id,ParticipationCodeRequest request,CancellationToken ct)=>Run("Verify",id,request,null,ct);
 private async Task<IActionResult> Run(string action,Guid id,object input,string? match,CancellationToken ct)
 {
  long? expected=null;
  if(action is "Access" or "Rotate" or "RevokeCode")
  {
   if(match is null)return Problem(statusCode:428,title:"If-Match is required.",extensions:new Dictionary<string,object?>{["code"]="PRECONDITION_REQUIRED"});
   if(match.Length<10 || !match.StartsWith("\"access-") || match[^1]!='"' || !long.TryParse(match.AsSpan(8,match.Length-9),System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var revision) || revision<1)return Problem(statusCode:400,title:"Use the quoted access ETag from GET.",extensions:new Dictionary<string,object?>{["code"]="INVALID_IF_MATCH"});
   expected=revision;
  }
  var result=await service.Execute(action,User.GetActorId(),User.GetSessionFamilyId(),id,input,expected,ct);
  if(!result.IsSuccess)return Problem(statusCode:result.Error!.Status,title:result.Error.Message,extensions:new Dictionary<string,object?>{["code"]=result.Error.Code,["errors"]=result.Error.Errors});
  if(result.Value.TryGetProperty("accessRevision",out var v))Response.Headers.ETag=$"\"access-{v.GetInt64()}\"";
  return Ok(result.Value);
 }
}
