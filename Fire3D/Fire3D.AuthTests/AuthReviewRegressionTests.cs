using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Application.Authentication.Commands.FirebaseLogin;
using Fire3D.Application.Users.Commands.RegisterDevice;
using Fire3D.Domain.Entities;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class AuthReviewRegressionTests
{
    [Theory]
    [InlineData("INVALID_FIREBASE_TOKEN", 401)]
    [InlineData("IDENTITY_UNAVAILABLE", 503)]
    public async Task Firebase_provider_errors_keep_their_status(string code, int status)
    {
        var provider = ResetProxy.For<IIdentityProvider>((_, _) =>
            Task.FromException<VerifiedIdentity>(new IdentityProviderException(code, "provider error", status)));
        var handler = new ExchangeFirebaseTokenCommandHandler(
            ResetProxy.For<IAuthStore>((_, _) => throw new Exception("store must not be reached")),
            ResetProxy.For<ITokenService>((_, _) => throw new Exception("token service must not be reached")),
            provider, TimeProvider.System);

        var result = await handler.Handle(new("firebase-token"), default);

        Assert.Equal(code, result.Error?.Code);
        Assert.Equal(status, result.Error?.Status);
    }

    [Fact]
    public async Task Firebase_cancellation_is_not_converted_to_invalid_token()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = ResetProxy.For<IIdentityProvider>((_, _) =>
            Task.FromException<VerifiedIdentity>(new OperationCanceledException(cancellation.Token)));
        var handler = new ExchangeFirebaseTokenCommandHandler(
            ResetProxy.For<IAuthStore>((_, _) => throw new Exception("store must not be reached")),
            ResetProxy.For<ITokenService>((_, _) => throw new Exception("token service must not be reached")),
            provider, TimeProvider.System);

        await Assert.ThrowsAsync<OperationCanceledException>(() => handler.Handle(new("firebase-token"), cancellation.Token));
    }

    [Fact]
    public async Task Device_identifier_accepts_non_guid_values_for_register_and_revoke()
    {
        var store = ResetProxy.For<IAuthStore>((method, _) => method switch
        {
            nameof(IAuthStore.UpsertDeviceAsync) => Task.FromResult(true),
            nameof(IAuthStore.RevokeDeviceAsync) => Task.CompletedTask,
            _ => throw new Exception("Unexpected store call: " + method)
        });
        var userId = Guid.NewGuid();

        var register = await new RegisterDeviceCommandHandler(store)
            .Handle(new(userId, "test-device-001", null, "Test device", "Windows 11"), default);
        var revoke = await new RevokeDeviceCommandHandler(store, TimeProvider.System)
            .Handle(new(userId, "test-device-001"), default);

        Assert.True(register.IsSuccess);
        Assert.True(revoke.IsSuccess);
    }

    [Fact]
    public async Task Device_validation_rejects_empty_or_oversized_values()
    {
        var store = ResetProxy.For<IAuthStore>((_, _) => throw new Exception("invalid input must not reach store"));
        var handler = new RegisterDeviceCommandHandler(store);

        var empty = await handler.Handle(new(Guid.NewGuid(), "", null, null, null), default);
        var oversized = await handler.Handle(new(Guid.NewGuid(), new string('x', 256), null, null, null), default);

        Assert.Equal("VALIDATION_ERROR", empty.Error?.Code);
        Assert.Equal("VALIDATION_ERROR", oversized.Error?.Code);
    }
}
