using Fire3D.Application.Authentication;
using Fire3D.Application.Users.Commands.RegisterDevice;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class DeviceLifecycleTests
{
    [Theory]
    [InlineData("not-a-uuid", "AQID")]
    [InlineData("93d1c906-a8ff-4322-a3e8-dfd415c65593", "short")]
    public async Task Register_device_rejects_invalid_installation_proof_before_store_access(string deviceUuid, string installationKey)
    {
        var store = ResetProxy.For<IAuthStore>((method, _) => throw new InvalidOperationException("Store must not be called: " + method));

        var result = await new RegisterDeviceCommandHandler(store, TimeProvider.System).Handle(
            new RegisterDeviceCommand(Guid.NewGuid(), deviceUuid, installationKey, null, null, null, null), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
    }

    [Fact]
    public async Task Revoke_device_requires_a_valid_uuid()
    {
        var store = ResetProxy.For<IAuthStore>((method, _) => throw new InvalidOperationException("Store must not be called: " + method));
        var result = await new RevokeDeviceCommandHandler(store, TimeProvider.System)
            .Handle(new(Guid.NewGuid(), "invalid"), default);
        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
    }
}
