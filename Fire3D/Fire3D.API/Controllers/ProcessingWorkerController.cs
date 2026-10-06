using System.Text.Json;
using Fire3D.API.Authorization;
using Fire3D.Application.Ifc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
namespace Fire3D.API.Controllers;
/// <summary>Machine-only gates. Fire3D user JWTs do not authorize these operations.</summary>
[ApiController,Route("internal/processing/jobs/{jobId:guid}"),Authorize(AuthenticationSchemes=ProcessingWorkerAuthentication.SchemeName)]
[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class ProcessingWorkerController(IProcessingWorkerGate gate,IOptions<ProcessingWorkerOptions> options):ControllerBase
{
    [HttpPost("claim")]
    public Task<IActionResult> Claim(Guid jobId,JsonElement input,CancellationToken ct)=>Run("Claim",jobId,input,ct);
    [HttpPost("renew")]
    public Task<IActionResult> Renew(Guid jobId,JsonElement input,CancellationToken ct)=>Run("Renew",jobId,input,ct);
    [HttpPost("outputs")]
    public Task<IActionResult> Output(Guid jobId,JsonElement input,CancellationToken ct)=>Run("Output",jobId,input,ct);
    [HttpPost("complete")]
    public Task<IActionResult> Complete(Guid jobId,JsonElement input,CancellationToken ct)=>Run("Complete",jobId,input,ct);
    [HttpPost("fail")]
    public Task<IActionResult> Fail(Guid jobId,JsonElement input,CancellationToken ct)=>Run("Fail",jobId,input,ct);
    private async Task<IActionResult> Run(string action,Guid job,JsonElement input,CancellationToken ct)
    {
        var errors=new Dictionary<string,string[]>();
        if(input.ValueKind!=JsonValueKind.Object)return Problem(statusCode:400,title:"A worker command object is required.",extensions:new Dictionary<string,object?> { ["code"]="VALIDATION_ERROR" });
        if(action=="Claim")
        {
            if(!Text(input,"eventKey",255))errors["eventKey"]=["A delivery event key is required."];
            foreach(var name in new[]{"payloadHash","inputHash"})if(!Hash(input,name))errors[name]=["A SHA-256 hash is required."];
            if(!Text(input,"toolchainVersion",100)||!options.Value.AllowedToolchains.Contains(input.GetProperty("toolchainVersion").GetString(),StringComparer.Ordinal))errors["toolchainVersion"]=["The toolchain must be explicitly allowed by this deployment."];
        }
        else
        {
            foreach(var name in new[]{"attemptId","leaseToken"})if(!input.TryGetProperty(name,out var id)||id.ValueKind!=JsonValueKind.String||!id.TryGetGuid(out var uuid)||uuid==Guid.Empty)errors[name]=["A current attempt and lease token are required."];
            if(action=="Complete"&&!Hash(input,"outputHash"))errors["outputHash"]=["Use the aggregate outputHash returned by the output gate."];
            if(action=="Fail"&&!Text(input,"reason",1000))errors["reason"]=["A failure reason of 1-1000 characters is required."];
            if(action=="Output")
            {
                if(!input.TryGetProperty("output",out var output)||output.ValueKind!=JsonValueKind.Object)errors["output"]=["An output object is required."];
                else
                {
                    foreach(var name in new[]{"artifactType","objectKey","schemaVersion","validatorVersion"})if(!Text(output,name,name=="objectKey"?1024:100))errors["output."+name]=["A nonempty bounded value is required."];
                    if(!Hash(output,"sha256Hash"))errors["output.sha256Hash"]=["A SHA-256 hash is required."];
                    if(!output.TryGetProperty("sizeBytes",out var size)||!size.TryGetInt64(out var bytes)||bytes<=0)errors["output.sizeBytes"]=["A positive size is required."];
                    if(!output.TryGetProperty("outcome",out var outcome)||outcome.ValueKind!=JsonValueKind.String||outcome.GetString() is not("Passed" or "Failed"))errors["output.outcome"]=["Use Passed or Failed."];
                    if(!output.TryGetProperty("metadata",out var metadata)||metadata.ValueKind!=JsonValueKind.Object)errors["output.metadata"]=["Metadata must be an object."];
                    if(!output.TryGetProperty("issues",out var issues)||issues.ValueKind!=JsonValueKind.Array||issues.GetArrayLength()>1000)errors["output.issues"]=["Issues must be an array with at most 1000 entries."];
                    else
                    {
                        var index=0;
                        foreach(var issue in issues.EnumerateArray())
                        {
                            var path=$"output.issues[{index++}]";
                            if(issue.ValueKind!=JsonValueKind.Object) { errors[path]=["Each issue must be an object."];continue; }
                            if(!Text(issue,"severity",20)||issue.GetProperty("severity").GetString() is not("Info" or "Warning" or "Error" or "Critical")) errors[path+".severity"]=["Use Info, Warning, Error or Critical."];
                            if(!Text(issue,"code",100))errors[path+".code"]=["A code of 1-100 characters is required."];
                            if(!Text(issue,"message",10000))errors[path+".message"]=["A message of 1-10000 characters is required."];
                            if(issue.TryGetProperty("details",out var details)&&details.ValueKind!=JsonValueKind.Object)errors[path+".details"]=["Details must be an object."];
                        }
                    }
                }
            }
        }
        if(errors.Count>0)return Problem(statusCode:400,title:"Worker command validation failed.",extensions:new Dictionary<string,object?> { ["code"]="VALIDATION_ERROR",["errors"]=errors });
        var result=await gate.ExecuteAsync(action,job,input,ct);
        return result.IsSuccess?Ok(result.Value):Problem(statusCode:result.Error!.Status,title:result.Error.Message,extensions:new Dictionary<string,object?> { ["code"]=result.Error.Code });
    }
    private static bool Text(JsonElement input,string name,int max)=>input.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String&&value.GetString() is { Length: > 0 } text&&text.Length<=max&&!string.IsNullOrWhiteSpace(text)&&!text.Any(char.IsControl);
    private static bool Hash(JsonElement input,string name)=>Text(input,name,64)&&input.GetProperty(name).GetString() is { Length:64 } hash&&hash.All(char.IsAsciiHexDigit);
}
