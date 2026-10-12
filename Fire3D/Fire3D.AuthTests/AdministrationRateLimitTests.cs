using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.API.Controllers;
using Fire3D.API.Extensions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class AdministrationRateLimitTests
{
    [Fact]
    public async Task Administration_limit_returns_problem_details_with_code_and_retry_after()
    {
        var sender=ResetProxy.For<ISender>((_,_)=>throw new InvalidOperationException("Unauthenticated requests never reach the handler."));
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Jwt:Issuer"]="test",["Jwt:Audience"]="test",["Jwt:SigningKey"]=Convert.ToBase64String(new byte[64])
        }).Build();
        using var host=await new HostBuilder().ConfigureWebHost(web=>web.UseTestServer().ConfigureServices(services=>
        {
            services.AddLogging();services.AddRouting();
            services.AddControllers().AddApplicationPart(typeof(AccountsController).Assembly);
            services.AddAccountAuthentication(config);services.AddSingleton(sender);
        }).Configure(app=>
        {
            app.UseRouting();app.UseRateLimiter();app.UseAuthentication();app.UseAuthorization();
            app.UseEndpoints(endpoints=>endpoints.MapControllers());
        })).StartAsync();
        using var client=host.GetTestClient();
        for(var i=0;i<120;i++)Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/accounts")).StatusCode);
        var rejected=await client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.TooManyRequests,rejected.StatusCode);
        Assert.Equal("application/problem+json",rejected.Content.Headers.ContentType?.MediaType);
        Assert.True(rejected.Headers.RetryAfter?.Delta>TimeSpan.Zero);
        var body=await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(429,body.GetProperty("status").GetInt32());
        Assert.Equal("ADMINISTRATION_RATE_LIMITED",body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }
}
