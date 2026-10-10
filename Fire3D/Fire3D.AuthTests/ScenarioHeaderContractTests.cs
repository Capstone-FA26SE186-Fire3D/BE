using Fire3D.API.Controllers;
using Fire3D.Application.Scenarios.Dto;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;
namespace Fire3D.AuthTests;
public sealed class ScenarioHeaderContractTests
{
    [Theory]
    [InlineData(null,428)]
    [InlineData("\"abc\"",400)]
    public async Task Draft_if_match_distinguishes_missing_and_malformed(string? header,int status)
    {
        var sender=ResetProxy.For<ISender>((_,_)=>throw new Exception("Invalid header must not reach mutation"));
        var controller=new ScenariosController(sender, ResetProxy.For<Fire3D.Application.Scenarios.IScenarioReviewQueries>((_,_)=>throw new Exception("Review reads must not run for draft mutations"))){ControllerContext=new ControllerContext{HttpContext=new DefaultHttpContext{User=new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub",Guid.NewGuid().ToString())],"test"))}}};
        var result=await controller.UpdateScenarioDraft(Guid.NewGuid(),System.Text.Json.JsonSerializer.SerializeToNode(new ScenarioDraftStateDto([],[],new(0,60,0),new([]))),header,default);
        Assert.Equal(status,Assert.IsType<ObjectResult>(result).StatusCode);
    }
}
