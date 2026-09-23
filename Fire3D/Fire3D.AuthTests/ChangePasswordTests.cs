using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.ChangePassword;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class ChangePasswordTests
{
    [Theory]
    [InlineData("", "New-Example-Password-2026!", "INVALID_CURRENT_PASSWORD")]
    [InlineData("current", "short", "INVALID_PASSWORD")]
    public async Task Invalid_request_does_not_reach_password_store(string currentPassword, string newPassword, string code)
    {
        var store = ResetProxy.For<ILocalPasswordReset>((_, _) => throw new Exception("Unexpected mutation"));
        var result = await new ChangePasswordCommandHandler(store)
            .Handle(new(Guid.NewGuid(), currentPassword, newPassword), default);

        Assert.Equal(code, result.Error?.Code);
    }

    [Fact]
    public async Task Valid_request_uses_authenticated_actor_only()
    {
        var actorId = Guid.NewGuid();
        var store = ResetProxy.For<ILocalPasswordReset>((method, args) =>
        {
            Assert.Equal(nameof(ILocalPasswordReset.ChangeAsync), method);
            Assert.Equal(actorId, args[0]);
            Assert.Equal("current-password", args[1]);
            Assert.Equal("New-Example-Password-2026!", args[2]);
            return Task.FromResult(AuthResult<bool>.Ok(true));
        });

        var result = await new ChangePasswordCommandHandler(store)
            .Handle(new(actorId, "current-password", "New-Example-Password-2026!"), default);

        Assert.True(result.IsSuccess);
    }
}
