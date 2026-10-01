using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Fire3D.API.Controllers;
using Fire3D.API.Extensions;
using Fire3D.Application.Authentication;
using Fire3D.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Fire3D.AuthTests;

public class AnonymousAuthHttpTests
{
    [Fact]
    public async Task Resend_verification_dispatches_a_registration_otp_request()
    {
        RequestRegistrationOtpCommand? dispatched = null;
        var sender = ResetProxy.For<ISender>((_, args) =>
        {
            dispatched = args.OfType<RequestRegistrationOtpCommand>().SingleOrDefault();
            return dispatched is null
                ? throw new InvalidOperationException("Resend must dispatch the registration OTP command.")
                : Task.FromResult(AuthResult<bool>.Ok(true));
        });
        var controller = new AuthController(sender)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.ResendVerification(new RequestRegistrationOtpRequest("user@example.test"), default);

        Assert.IsType<AcceptedResult>(result);
        Assert.NotNull(dispatched);
        Assert.Equal("user@example.test", dispatched.Email);
    }

    [Fact]
    public async Task Registration_otp_rate_limit_returns_a_positive_retry_after_header()
    {
        var sender = ResetProxy.For<ISender>((_, _) =>
            Task.FromResult(AuthResult<bool>.Fail("OTP_RATE_LIMITED", "Too many verification requests.", 429)));
        var http = new DefaultHttpContext();
        var controller = new AuthController(sender)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        var result = await controller.RequestRegistrationOtp(new RequestRegistrationOtpRequest("user@example.test"), default);

        Assert.IsType<ObjectResult>(result);
        Assert.True(int.TryParse(http.Response.Headers.RetryAfter.ToString(), out var seconds));
        Assert.True(seconds > 0);
    }

    [Theory]
    [InlineData("login",false,200)] [InlineData("login",true,200)]
    [InlineData("register",false,201)] [InlineData("register",true,201)]
    [InlineData("forgot-password",false,202)] [InlineData("forgot-password",true,202)]
    [InlineData("reset-password",false,204)] [InlineData("reset-password",true,204)]
    [InlineData("resend-verification",false,202)] [InlineData("resend-verification",true,202)]
    [InlineData("verify-email",false,204)] [InlineData("verify-email",true,204)]
    [InlineData("registration/request-otp",false,202)] [InlineData("registration/request-otp",true,202)]
    [InlineData("registration/verify-otp",false,200)] [InlineData("registration/verify-otp",true,200)]
    [InlineData("login-firebase",false,200)] [InlineData("login-firebase",true,200)]
    public async Task Public_auth_routes_reach_handler_without_valid_bearer(string route,bool badBearer,int status)
    {
        var calls=0;
        var account=new AccountResponse(Guid.NewGuid(),"user@example.test","Test",UserRole.Trainee,null);
        var sender=ResetProxy.For<ISender>((_,_)=> {
            calls++;
            return route switch {
                "login" => (object)Task.FromResult(AuthResult<LoginResponse>.Ok(new("access","refresh",account))),
                "register" => Task.FromResult(AuthResult<AccountResponse>.Ok(account)),
                "login-firebase" => Task.FromResult(AuthResult<GoogleExchangeResponse>.Ok(new("Authenticated",new("access","refresh",account)))),
                "registration/verify-otp" => Task.FromResult(AuthResult<RegistrationOtpVerificationResponse>.Ok(new("registration-proof", DateTime.UtcNow.AddMinutes(15)))),
                _ => Task.FromResult(AuthResult<bool>.Ok(true))
            };
        });
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Jwt:Issuer"]="test",["Jwt:Audience"]="test",["Jwt:SigningKey"]=Convert.ToBase64String(new byte[64]),
        }).Build();
        using var host=await new HostBuilder().ConfigureWebHost(web=>web.UseTestServer().ConfigureServices(services=> {
            services.AddLogging();services.AddRouting();
            services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
            services.AddAccountAuthentication(config);
            services.RemoveAll<IHostedService>();
            services.AddSingleton(sender);
        }).Configure(app=> {
            app.UseRouting();app.UseRateLimiter();app.UseAuthentication();app.UseAuthorization();
            app.UseEndpoints(e=>e.MapControllers());
        })).StartAsync();
        using var client=host.GetTestClient();
        if(badBearer)client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer","invalid-expired-token");
        object body=route=="login-firebase" ? "firebase-id-token" : new {
            email="user@example.test",username="test-user",password="StrongPassword12!",confirmPassword="StrongPassword12!",fullName="Test",token=new string('a',64),otp="123456",newPassword="Replacement12!"
        };
        var response=await client.PostAsJsonAsync("/api/auth/"+route,body);
        Assert.Equal(status,(int)response.StatusCode);Assert.Equal(1,calls);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(1,calls);
    }
}
