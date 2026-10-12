using System.Security.Claims;
using Fire3D.API.Controllers;
using Fire3D.Application.Authentication;
using Fire3D.Application.Ifc;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class RetryKeyTests
{
    private static (IfcCommandsController Controller, List<RetryProcessingJobCommand> Sent) Controller()
    {
        var sent=new List<RetryProcessingJobCommand>();
        var sender=ResetProxy.For<ISender>((_,args)=>
        {
            var command=(RetryProcessingJobCommand)args[0]!;sent.Add(command);
            return Task.FromResult(AuthResult<RetryProcessingJobResponse>.Ok(new(command.JobId,"Requeued")));
        });
        var user=new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub,Guid.NewGuid().ToString())],"test"));
        return (new IfcCommandsController(sender){ControllerContext=new ControllerContext{HttpContext=new DefaultHttpContext{User=user}}},sent);
    }

    [Fact]
    public async Task Retry_takes_its_key_from_the_header_or_the_body_but_never_two_different_keys()
    {
        var job=Guid.NewGuid();var key=Guid.NewGuid();
        var (controller,sent)=Controller();
        Assert.IsType<AcceptedResult>((await controller.RetryJob(job,new(Guid.Empty,"Fix config"),default,key.ToString())).Result);
        Assert.Equal(key,sent.Single().Request.RequestId);
        // Body-only stays supported; the same key in both places is accepted.
        Assert.IsType<AcceptedResult>((await controller.RetryJob(job,new(key,"Fix config"),default)).Result);
        Assert.IsType<AcceptedResult>((await controller.RetryJob(job,new(key,"Fix config"),default,key.ToString())).Result);
        Assert.Equal(3,sent.Count);
        // A non-UUID header or two different keys never reach the gate.
        foreach(var (body,header) in new[]{(Guid.Empty,"retry-1"),(Guid.NewGuid(),key.ToString()),(Guid.Empty,Guid.Empty.ToString())})
        {
            var problem=Assert.IsType<ObjectResult>((await controller.RetryJob(job,new(body,"Fix config"),default,header)).Result);
            Assert.Equal(400,problem.StatusCode);Assert.Equal("VALIDATION_ERROR",((ProblemDetails)problem.Value!).Extensions["code"]);
        }
        Assert.Equal(3,sent.Count);
    }
}
