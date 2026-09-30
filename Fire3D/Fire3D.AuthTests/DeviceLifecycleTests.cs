using Fire3D.Application.Authentication;
using Fire3D.Application.Users.Commands.RegisterDevice;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class DeviceLifecycleTests
{
    [Fact]
    public void Installation_proof_keeps_legacy_hash_for_a_safe_one_time_upgrade()
    {
        var key = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var proof = DeviceInstallationProof.Create(key);

        Assert.Equal("630dcd2966c4336691125448bbb25b4ff412a49c732db2c8abc1b8581bd710dd", proof.CurrentHash);
        Assert.Equal("ea866a757e4c38babfa8127cbe9a409d3e1f93a00ff1488ff735fcf917afffd0", proof.LegacyHash);
        Assert.NotEqual(proof.CurrentHash, proof.LegacyHash);
        Assert.True(proof.Matches(proof.LegacyHash, null));
        Assert.True(proof.Matches(proof.CurrentHash, DeviceInstallationProof.CurrentHashScheme));
        Assert.False(proof.Matches(proof.LegacyHash, DeviceInstallationProof.CurrentHashScheme));
    }

    [Theory]
    [InlineData("not-a-uuid", "AQID")]
    [InlineData("93d1c906-a8ff-4322-a3e8-dfd415c65593", "short")]
    public async Task Register_device_rejects_invalid_installation_proof_before_store_access(string deviceUuid, string installationKey)
    {
        var store = ResetProxy.For<IAuthStore>((method, _) => throw new InvalidOperationException("Store must not be called: " + method));

        var result = await new RegisterDeviceCommandHandler(store, TimeProvider.System).Handle(
            new RegisterDeviceCommand(Guid.NewGuid(), Guid.NewGuid(), deviceUuid, installationKey, null, null, null, null), default);

        Assert.False(result.IsSuccess);
        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
    }

    [Fact]
    public async Task Revoke_device_requires_a_valid_uuid()
    {
        var store = ResetProxy.For<IAuthStore>((method, _) => throw new InvalidOperationException("Store must not be called: " + method));
        var result = await new RevokeDeviceCommandHandler(store, TimeProvider.System)
            .Handle(new(Guid.NewGuid(), Guid.NewGuid(), "invalid", "short"), default);
        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
    }

    [Fact]
    public async Task Register_device_rejects_a_session_revoked_by_logout_all_before_binding_push()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "device@example.test", Role = UserRole.Trainee, IsActive = true };
        var transaction = ResetProxy.For<IAuthTransaction>((method, _) => method switch
        {
            "DisposeAsync" => ValueTask.CompletedTask,
            _ => throw new InvalidOperationException(method)
        });
        var store = ResetProxy.For<IAuthStore>((method, _) => method switch
        {
            "BeginUserTransactionAsync" => Task.FromResult(transaction),
            "FindUserAsync" => Task.FromResult<User?>(user),
            "FamilyIsActiveAsync" => Task.FromResult(false),
            _ => throw new InvalidOperationException("Unexpected store call: " + method)
        });

        var key = Convert.ToBase64String(new byte[32]).TrimEnd('=');
        var result = await new RegisterDeviceCommandHandler(store, TimeProvider.System).Handle(
            new(user.Id, Guid.NewGuid(), Guid.NewGuid().ToString("D"), key, "token", "Samsung Galaxy", "Android 15", "1.0"), default);

        Assert.Equal("UNAUTHORIZED", result.Error?.Code);
    }
}
