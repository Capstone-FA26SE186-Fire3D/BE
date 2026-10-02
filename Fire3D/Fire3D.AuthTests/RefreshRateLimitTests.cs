using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.API.Controllers;
using Fire3D.API.Extensions;
using Fire3D.Application.Authentication;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class RefreshRateLimitTests
{
    [Fact]
    public async Task Refresh_limits_by_remote_ip_and_returns_safe_problem_details_before_the_handler()
    {
        var calls=0;
        var sender=ResetProxy.For<ISender>((_,_)=>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(AuthResult<TokenResponse>.Fail("REFRESH_TOKEN_INVALID","Invalid refresh token.",401));
        });
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Jwt:Issuer"]="test",["Jwt:Audience"]="test",["Jwt:SigningKey"]=Convert.ToBase64String(new byte[64])
        }).Build();
        using var host=await new HostBuilder().ConfigureWebHost(web=>web.UseTestServer().ConfigureServices(services=>
        {
            services.AddLogging();services.AddRouting();
            services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
            services.AddAccountAuthentication(config);services.AddSingleton(sender);
        }).Configure(app=>
        {
            // Test-only transport supplies distinct connection addresses, never production header parsing.
            app.Use((context,next)=>
            {
                context.Connection.RemoteIpAddress=IPAddress.Parse(context.Request.Headers["X-Test-Remote-IP"].FirstOrDefault()??"192.0.2.1");
                return next(context);
            });
            app.UseRouting();app.UseRateLimiter();app.UseAuthentication();app.UseAuthorization();
            app.UseEndpoints(endpoints=>endpoints.MapControllers());
        })).StartAsync();
        using var client=host.GetTestClient();
        for(var i=0;i<10;i++)Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/api/auth/refresh",new{refreshToken="invalid"})).StatusCode);
        client.DefaultRequestHeaders.Add("X-Forwarded-For","198.51.100.99");
        var rejected=await client.PostAsJsonAsync("/api/auth/refresh",new{refreshToken="invalid"});
        Assert.Equal(HttpStatusCode.TooManyRequests,rejected.StatusCode);Assert.Equal(10,calls);
        Assert.Equal("application/problem+json",rejected.Content.Headers.ContentType?.MediaType);
        Assert.True(rejected.Headers.RetryAfter?.Delta>TimeSpan.Zero);
        var body=await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(429,body.GetProperty("status").GetInt32());
        Assert.Equal("AUTH_REFRESH_RATE_LIMITED",body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("invalid",body.GetRawText(),StringComparison.OrdinalIgnoreCase);
        client.DefaultRequestHeaders.Add("X-Test-Remote-IP","192.0.2.2");
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/api/auth/refresh",new{refreshToken="invalid"})).StatusCode);
        Assert.Equal(11,calls);
    }
}
