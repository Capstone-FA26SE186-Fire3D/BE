using System.Net;
using System.Reflection;
using Fire3D.API.Controllers;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.ForgotPassword;
using Fire3D.Application.Authentication.Commands.ResetPassword;
using Fire3D.Domain.Entities;
using Fire3D.Infrastructure.Authentication;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
namespace Fire3D.AuthTests;

public class ResetProxy : DispatchProxy
{
    public Func<string,object?[],object?> Call=null!;
    protected override object? Invoke(MethodInfo? m,object?[]? args)=>Call(m!.Name,args??[]);
    public static T For<T>(Func<string,object?[],object?> call) where T:class
    { var p=Create<T,ResetProxy>();((ResetProxy)(object)p).Call=call;return p; }
}
public class PasswordResetTests
{
    private static T Unexpected<T>() where T:class => ResetProxy.For<T>((m,_)=>throw new Exception("Unexpected: "+m));
    [Theory]
    [InlineData("")] [InlineData("@")] [InlineData("a@")] [InlineData("a b@example.test")]
    [InlineData("a@@example.test")] [InlineData("Name <a@example.test>")]
    public async Task Invalid_email_does_not_enqueue(string email)
    {
        var result=await new ForgotPasswordCommandHandler(Unexpected<IAuthStore>()).Handle(new(email),default);
        Assert.Equal(400,result.Error?.Status);
    }
    [Fact]
    public async Task Email_normalized_without_account_lookup()
    {
        var store=ResetProxy.For<IAuthStore>((method,args)=>{
            Assert.Equal("EnqueuePasswordResetAsync",method);Assert.Equal("a@example.test",args[0]);return Task.CompletedTask;});
        Assert.True((await new ForgotPasswordCommandHandler(store).Handle(new(" A@EXAMPLE.TEST "),default)).IsSuccess);
    }
    [Theory] [InlineData(11)] [InlineData(129)]
    public async Task Invalid_password_never_reaches_provider(int length)
    {
        var result=await new ResetPasswordCommandHandler(Unexpected<IPasswordResetProvider>(),Unexpected<IAuthStore>(),Unexpected<IPasswordResetStore>())
            .Handle(new("code",new string('a',length)),default);
        Assert.Equal("INVALID_PASSWORD",result.Error?.Code);
    }
    [Fact]
    public async Task Verification_outage_stays_503()
    {
        var provider=ResetProxy.For<IPasswordResetProvider>((_,_)=>Task.FromException<VerifiedResetIdentity>(PasswordResetException.Unavailable()));
        var r=await new ResetPasswordCommandHandler(provider,Unexpected<IAuthStore>(),Unexpected<IPasswordResetStore>()).Handle(new("code","LongPassword12!"),default);
        Assert.Equal(503,r.Error?.Status);
    }
    [Theory] [InlineData("Success","Completed",null)] [InlineData("Invalid","Rejected",400)] [InlineData("Unknown",null,503)]
    public async Task Reset_state_is_durable_before_mutation_and_unknown_outcome_stays_fenced(string mode,string? finish,int? status)
    {
        var user=new User{Id=Guid.NewGuid(),FirebaseUid="uid",IsActive=true};var operation=Guid.NewGuid();var calls=new List<string>();
        var provider=ResetProxy.For<IPasswordResetProvider>((method,_)=>{
            calls.Add(method);
            if(method=="VerifyResetCodeAsync")return Task.FromResult(new VerifiedResetIdentity(user.Id,"uid"));
            Assert.Contains("BeginAsync",calls);
            return mode switch {"Invalid"=>Task.FromException(PasswordResetException.InvalidCode()),"Unknown"=>Task.FromException(PasswordResetException.Unavailable()),_=>Task.CompletedTask};});
        var accounts=ResetProxy.For<IAuthStore>((_,_)=>Task.FromResult<User?>(user));
        var resets=ResetProxy.For<IPasswordResetStore>((method,args)=>{
            calls.Add(method);
            if(method=="BeginAsync"){Assert.Equal(64,((string)args[2]!).Length);Assert.DoesNotContain("code",(string)args[2]!);return Task.FromResult<Guid?>(operation);}
            Assert.Equal(finish,args[2]);return Task.CompletedTask;});
        var result=await new ResetPasswordCommandHandler(provider,accounts,resets).Handle(new("code","LongPassword12!"),default);
        Assert.Equal(status,result.Error?.Status);
        Assert.Equal(finish is not null,calls.Contains("FinishAsync"));
    }
    [Fact]
    public async Task Identity_mismatch_does_not_mutate_password()
    {
        var user=new User{Id=Guid.NewGuid(),FirebaseUid="other",IsActive=true};
        var provider=ResetProxy.For<IPasswordResetProvider>((method,_)=>method=="VerifyResetCodeAsync"?Task.FromResult(new VerifiedResetIdentity(user.Id,"uid")):throw new Exception("Unexpected mutation"));
        var accounts=ResetProxy.For<IAuthStore>((_,_)=>Task.FromResult<User?>(user));
        Assert.Equal(400,(await new ResetPasswordCommandHandler(provider,accounts,Unexpected<IPasswordResetStore>()).Handle(new("code","LongPassword12!"),default)).Error?.Status);
    }
    [Theory] [InlineData(false,400)] [InlineData(true,503)]
    public async Task Controllers_preserve_error_status(bool reset,int status)
    {
        var sender=ResetProxy.For<ISender>((_,_)=>Task.FromResult(AuthResult<bool>.Fail("TEST","Test error",status)));
        var services=new ServiceCollection();services.AddLogging();services.AddControllers();
        using var sp=services.BuildServiceProvider();
        var controller=new AuthController(sender){ControllerContext=new(){HttpContext=new DefaultHttpContext{RequestServices=sp}}};
        var result=reset?await controller.ResetPassword(new("code","LongPassword12!"),default):await controller.ForgotPassword(new("@"),default);
        var problem=Assert.IsType<ObjectResult>(result);Assert.Equal(status,problem.StatusCode);
        Assert.Equal("TEST",Assert.IsType<ProblemDetails>(problem.Value).Extensions["code"]);
    }
    [Theory] [InlineData(false,202)] [InlineData(true,204)]
    public async Task Controllers_success_status(bool reset,int status)
    {
        var sender=ResetProxy.For<ISender>((_,_)=>Task.FromResult(AuthResult<bool>.Ok(true)));
        var controller=new AuthController(sender);
        var result=reset?await controller.ResetPassword(new("code","LongPassword12!"),default):await controller.ForgotPassword(new("a@example.test"),default);
        Assert.Equal(status,result is ObjectResult o?o.StatusCode:((StatusCodeResult)result).StatusCode);
    }
    private sealed class Transport(HttpStatusCode status,string body) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(status){Content=new StringContent(body)}); }
    private static FirebasePasswordResetProvider Provider(HttpStatusCode status,string body) => new(new HttpClient(new Transport(status,body)),Unexpected<IAuthStore>(),
        Options.Create(new AuthEmailOptions{FirebaseApiKey="test",FrontendUrl="https://example.test"}),Unexpected<IFirebaseResetAdmin>());
    [Theory]
    [InlineData(400,"INVALID_OOB_CODE",400)] [InlineData(400,"EXPIRED_OOB_CODE",400)]
    [InlineData(400,"WEAK_PASSWORD : policy",400)] [InlineData(429,"TOO_MANY_ATTEMPTS_TRY_LATER",503)]
    [InlineData(503,"UNAVAILABLE",503)] [InlineData(400,"API_KEY_INVALID",503)]
    public async Task Provider_maps_only_known_client_errors(int http,string code,int expected)
    {
        var ex=await Assert.ThrowsAsync<PasswordResetException>(()=>Provider((HttpStatusCode)http,"{\"error\":{\"message\":\""+code+"\"}}").ConfirmResetAsync("code","LongPassword12!",default));
        Assert.Equal(expected,ex.Error.Status);
    }
    [Fact]
    public async Task Invalid_provider_response_is_unavailable()
    {Assert.Equal(503,(await Assert.ThrowsAsync<PasswordResetException>(()=>Provider(HttpStatusCode.BadGateway,"not json").ConfirmResetAsync("code","LongPassword12!",default))).Error.Status);}
    [Fact]
    public async Task Email_link_uses_frontend_and_google_only_is_rejected()
    {
        bool password=true;
        var admin=ResetProxy.For<IFirebaseResetAdmin>((method,_)=>method=="FindAsync"?Task.FromResult(new ResetFirebaseUser("uid",password,false)):
            Task.FromResult("https://example.firebaseapp.com/__/auth/action?mode=resetPassword&oobCode=opaque%2Bcode"));
        var p=new FirebasePasswordResetProvider(new HttpClient(),Unexpected<IAuthStore>(),Options.Create(new AuthEmailOptions{FirebaseApiKey="test",FrontendUrl="https://app.example.test"}),admin);
        Assert.Equal("https://app.example.test/reset-password?mode=resetPassword&oobCode=opaque%2Bcode",await p.GenerateResetLinkAsync("a@example.test",default));
        password=false;Assert.True((await Assert.ThrowsAsync<PasswordResetException>(()=>p.GenerateResetLinkAsync("a@example.test",default))).Permanent);
    }
}
