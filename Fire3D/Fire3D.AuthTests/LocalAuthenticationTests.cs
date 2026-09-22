using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.ResetPassword;
using Fire3D.Domain.Entities;
using Fire3D.Infrastructure.Authentication;
using Xunit;

namespace Fire3D.AuthTests;

public class LocalAuthenticationTests
{
    private sealed class Transaction : IAuthTransaction
    {
        public Task CommitAsync(CancellationToken ct)=>Task.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    [Fact]
    public async Task Google_cannot_link_an_existing_local_account_by_email()
    {
        var local=new User { Id=Guid.NewGuid(),Email="user@example.test",PasswordHash="existing",IsActive=true };
        var accounts=ResetProxy.For<IAuthStore>((method,_)=>method switch {
            "FindUserByFirebaseUidAsync" => Task.FromResult<User?>(null),
            "FindUserByEmailAsync" => Task.FromResult<User?>(local),
            "BeginUserTransactionAsync" => Task.FromResult<IAuthTransaction>(new Transaction()),
            _ => throw new Exception("Must not mutate account: "+method)
        });
        var provider=ResetProxy.For<Fire3D.Application.Authentication.Abstractions.IIdentityProvider>((_,_)=>
            Task.FromResult(new Fire3D.Application.Authentication.Abstractions.VerifiedIdentity("google-uid",local.Email)));
        var tokens=ResetProxy.For<ITokenService>((_,_)=>throw new Exception("Must not issue session"));
        var handler=new Fire3D.Application.Authentication.Commands.FirebaseLogin.ExchangeFirebaseTokenCommandHandler(accounts,tokens,provider,TimeProvider.System);
        Assert.Equal("ACCOUNT_LINK_REQUIRED",(await handler.Handle(new("id-token"),default)).Error?.Code);
        Assert.Null(local.FirebaseUid);
    }
    [Fact]
    public void Passwords_are_salted_and_verified_without_exposing_hash()
    {
        var service=new PasswordService();var user=new User();
        user.PasswordHash=service.Hash(user,"StrongPassword12!");
        Assert.NotEqual(user.PasswordHash,service.Hash(user,"StrongPassword12!"));
        Assert.True(service.Verify(user,"StrongPassword12!",out _));
        Assert.False(service.Verify(user,"wrong",out _));
        Assert.DoesNotContain("PasswordHash",System.Text.Json.JsonSerializer.Serialize(user));
    }
    [Theory] [InlineData(11)] [InlineData(129)]
    public async Task Bad_password_does_not_reach_reset_store(int length)
    {
        var store=ResetProxy.For<ILocalPasswordReset>((_,_)=>throw new Exception("Unexpected mutation"));
        var result=await new ResetPasswordCommandHandler(store).Handle(new(new string('a',64),new string('x',length)),default);
        Assert.Equal("INVALID_PASSWORD",result.Error?.Code);
    }
    [Fact]
    public async Task Firebase_oob_code_is_not_accepted_as_local_reset_token()
    {
        var store=ResetProxy.For<ILocalPasswordReset>((_,_)=>throw new Exception("Unexpected mutation"));
        Assert.Equal(400,(await new ResetPasswordCommandHandler(store).Handle(new("firebase-code","StrongPassword12!"),default)).Error?.Status);
    }
}
