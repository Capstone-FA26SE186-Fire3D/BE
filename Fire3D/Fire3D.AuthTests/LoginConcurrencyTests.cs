using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.LoginWithPassword;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class LoginConcurrencyTests
{
    private sealed class Passwords(List<string> calls,bool accepted=true) : IPasswordService
    {
        public string Hash(User user,string password){calls.Add("hash");return "rehash";}
        public bool Verify(User user,string password,out bool needsRehash){calls.Add("verify");needsRehash=false;return accepted;}
        public void VerifyDummy(string password)=>calls.Add("dummy");
    }
    [Theory]
    [InlineData("none",200)] [InlineData("password",401)] [InlineData("disabled",403)] [InlineData("email",401)] [InlineData("pending",403)]
    public async Task Password_work_precedes_lock_and_identity_is_rechecked(string concurrentChange,int status)
    {
        var calls=new List<string>();var user=new User{Id=Guid.NewGuid(),Email="login@example.test",PasswordHash="snapshot-hash",IsActive=true,Role=UserRole.Trainee};
        var current=new User{Id=user.Id,Email=user.Email,PasswordHash=concurrentChange=="password"?"new-hash":user.PasswordHash,IsActive=concurrentChange!="disabled",Role=user.Role};
        if(concurrentChange=="email")current.Email="changed@example.test";
        if(concurrentChange=="pending")current.RegistrationExpiresAt=DateTime.UtcNow.AddHours(1);
        var transaction=ResetProxy.For<IAuthTransaction>((method,_)=>method=="CommitAsync"?Task.CompletedTask:ValueTask.CompletedTask);
        var store=ResetProxy.For<IAuthStore>((method,args)=>{
            calls.Add(method);
            return method switch {
                "FindUserByEmailAsync"=>Task.FromResult<User?>(user),
                "FindUserAsync"=>Task.FromResult<User?>(current),
                "BeginUserTransactionAsync"=>Task.FromResult(transaction),
                "FinalizePasswordLoginAsync" or "UpdateLoginAsync" or "AddRefreshTokenAsync" or "WriteAuditAsync"=>Task.CompletedTask,
                _=>throw new InvalidOperationException(method)
            };});
        var tokens=ResetProxy.For<ITokenService>((method,_)=>method switch {
            "CreateAccessToken"=>new AccessTokenValue("access",DateTime.UtcNow.AddMinutes(15)),
            "CreateRefreshToken"=>"refresh", "HashRefreshToken"=>"hash", "get_RefreshTokenLifetime"=>TimeSpan.FromDays(7),
            _=>throw new InvalidOperationException(method)
        });
        var result=await new LoginWithPasswordCommandHandler(store,new Passwords(calls),tokens,TimeProvider.System).Handle(new(user.Email,"password"),default);
        Assert.Equal(status,result.IsSuccess?200:result.Error!.Status);
        Assert.True(calls.IndexOf("verify") < calls.IndexOf("BeginUserTransactionAsync"),"PBKDF2 must not hold the user lock.");
        if(status!=200)Assert.DoesNotContain("FinalizePasswordLoginAsync",calls);
    }
    [Fact]
    public async Task Incorrect_password_never_acquires_user_transaction()
    {
        var calls=new List<string>();
        var store=ResetProxy.For<IAuthStore>((method,_)=>method=="FindUserByEmailAsync"?Task.FromResult<User?>(new User{PasswordHash="hash"}):throw new InvalidOperationException(method));
        var tokens=ResetProxy.For<ITokenService>((method,_)=>throw new InvalidOperationException(method));
        var result=await new LoginWithPasswordCommandHandler(store,new Passwords(calls,false),tokens,TimeProvider.System).Handle(new("login@example.test","wrong"),default);
        Assert.Equal(401,result.Error!.Status);
    }
}
