using System.Text.Json;
using Fire3D.API.Controllers;
using Fire3D.Application.Ifc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;
namespace Fire3D.AuthTests;
public sealed class ProcessingWorkerValidationTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"attemptId\":\"11111111-1111-4111-8111-111111111111\",\"leaseToken\":\"22222222-2222-4222-8222-222222222222\",\"output\":{\"outcome\":123,\"issues\":[null]}}")]
    public async Task Malformed_worker_output_is_validation_error_before_database(string json)
    {
        var controller=new ProcessingWorkerController(ResetProxy.For<IProcessingWorkerGate>((_,_)=>throw new Exception("Invalid input must not reach gate")),Options.Create(new ProcessingWorkerOptions())){ControllerContext=new ControllerContext{HttpContext=new DefaultHttpContext()}};
        var action=await controller.Output(Guid.NewGuid(),JsonDocument.Parse(json).RootElement,default);
        Assert.Equal(400,Assert.IsType<ObjectResult>(action).StatusCode);
    }
}
