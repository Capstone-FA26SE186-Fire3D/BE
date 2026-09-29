using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.SelfRegistration;
using Fire3D.Infrastructure.Authentication;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class SelfRegistrationPasswordPolicyTests
{
    [Theory]
    [InlineData("12345")]
    [InlineData("      ")]
    public async Task Trainee_registration_rejects_invalid_password_before_any_persistence(string password)
    {
        var store = ResetProxy.For<IAuthStore>((method, _) => throw new InvalidOperationException("Unexpected store operation: " + method));
        var proof = ResetProxy.For<IRegistrationOtpService>((method, _) => throw new InvalidOperationException("Unexpected OTP operation: " + method));
        var handler = new RegisterTraineeCommandHandler(store, new PasswordService(), proof, TimeProvider.System);

        var result = await handler.Handle(new RegisterTraineeCommand("test@example.test", "test-user", password, password), default);

        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
        Assert.Contains("password", result.Error?.Errors?.Keys ?? []);
    }

    [Fact]
    public async Task Trainee_registration_accepts_a_six_character_password_and_exact_confirmation()
    {
        var transaction = ResetProxy.For<IAuthTransaction>((method, _) => method switch
        {
            "CommitAsync" => Task.CompletedTask,
            "DisposeAsync" => ValueTask.CompletedTask,
            _ => throw new InvalidOperationException("Unexpected transaction operation: " + method)
        });
        var store = ResetProxy.For<IAuthStore>((method, _) => method switch
        {
            "BeginUserTransactionAsync" => Task.FromResult(transaction),
            "TryCreateTraineeAsync" => Task.FromResult(RegisterConflict.None),
            "WriteAuditAsync" => Task.CompletedTask,
            _ => throw new InvalidOperationException("Unexpected store operation: " + method)
        });
        var proof = ResetProxy.For<IRegistrationOtpService>((method, _) => method == "ConsumeRegistrationTokenAsync"
            ? Task.FromResult(AuthResult<bool>.Ok(true)) : throw new InvalidOperationException("Unexpected OTP operation: " + method));
        var handler = new RegisterTraineeCommandHandler(store, new PasswordService(), proof, TimeProvider.System);

        var result = await handler.Handle(new RegisterTraineeCommand("test@example.test", "test-user", "123456", "123456", RegistrationToken: "proof"), default);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Trainee_registration_requires_an_exact_password_confirmation()
    {
        var store = ResetProxy.For<IAuthStore>((method, _) => throw new InvalidOperationException("Unexpected store operation: " + method));
        var proof = ResetProxy.For<IRegistrationOtpService>((method, _) => throw new InvalidOperationException("Unexpected OTP operation: " + method));
        var handler = new RegisterTraineeCommandHandler(store, new PasswordService(), proof, TimeProvider.System);

        var result = await handler.Handle(new RegisterTraineeCommand("test@example.test", "test-user", "123456", "123457"), default);

        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
        Assert.Contains("confirmPassword", result.Error?.Errors?.Keys ?? []);
    }
}
