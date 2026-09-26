using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.SelfRegistration;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Authentication;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class SelfRegistrationTests
{
    private sealed class Transaction(Action commit) : IAuthTransaction
    {
        public Task CommitAsync(CancellationToken ct) { commit(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Trainee_registration_normalizes_username_and_assigns_only_trainee_role()
    {
        User? persisted = null;
        var committed = false;
        var store = ResetProxy.For<IAuthStore>((method, args) => method switch
        {
            "BeginUserTransactionAsync" => Task.FromResult<IAuthTransaction>(new Transaction(() => committed = true)),
            "TryCreateTraineeAsync" => CaptureTrainee(args, user => persisted = user),
            "WriteAuditAsync" => Task.CompletedTask,
            _ => throw new InvalidOperationException("Unexpected store operation: " + method)
        });

        var handler = new RegisterTraineeCommandHandler(store, new PasswordService(), TimeProvider.System);
        var result = await handler.Handle(
            new RegisterTraineeCommand(" trainee@example.test ", "Fire.Drill", "StrongPassword12!", "StrongPassword12!", "Trainee"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("fire.drill", result.Value!.Username);
        Assert.True(committed);
        Assert.NotNull(persisted);
        Assert.Equal("fire.drill", persisted!.Username);
        Assert.Equal(UserRole.Trainee, persisted.Role);
        Assert.Null(persisted.OrganizationId);
        Assert.NotEqual("StrongPassword12!", persisted.PasswordHash);
    }

    [Fact]
    public async Task Trainee_registration_rejects_password_confirmation_mismatch_before_persistence()
    {
        var store = ResetProxy.For<IAuthStore>((method, _) => throw new InvalidOperationException("Unexpected store operation: " + method));
        var handler = new RegisterTraineeCommandHandler(store, new PasswordService(), TimeProvider.System);

        var result = await handler.Handle(
            new RegisterTraineeCommand("trainee@example.test", "trainee", "StrongPassword12!", "DifferentPassword12!", "Trainee"),
            CancellationToken.None);

        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
        Assert.Equal(400, result.Error?.Status);
    }

    [Fact]
    public async Task Organization_registration_creates_owner_and_organization_in_one_transaction()
    {
        Organization? persistedOrganization = null;
        User? persistedUser = null;
        var committed = false;
        var store = ResetProxy.For<IAuthStore>((method, args) => method switch
        {
            "BeginUserTransactionAsync" => Task.FromResult<IAuthTransaction>(new Transaction(() => committed = true)),
            "TryCreateOrganizationWithUserAsync" => CaptureOrganization(args, (organization, user) =>
            {
                persistedOrganization = organization;
                persistedUser = user;
            }),
            "WriteAuditAsync" => Task.CompletedTask,
            _ => throw new InvalidOperationException("Unexpected store operation: " + method)
        });

        var handler = new RegisterOrganizationCommandHandler(store, new PasswordService(), TimeProvider.System);
        var result = await handler.Handle(
            new RegisterOrganizationCommand("owner@example.test", "StrongPassword12!", "StrongPassword12!", "Owner", "Fire3D Co", "1 Fire Street", "+84 123456789"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(committed);
        Assert.NotNull(persistedOrganization);
        Assert.NotNull(persistedUser);
        Assert.Equal("1 Fire Street", persistedOrganization!.Address);
        Assert.Equal("+84 123456789", persistedOrganization.PhoneNumber);
        Assert.Equal(UserRole.OrganizationUser, persistedUser!.Role);
        Assert.Equal(persistedOrganization.Id, persistedUser.OrganizationId);
    }

    private static Task<RegisterConflict> CaptureTrainee(object?[] args, Action<User> capture)
    {
        capture((User)args[0]!);
        return Task.FromResult(RegisterConflict.None);
    }

    private static Task<RegisterConflict> CaptureOrganization(object?[] args, Action<Organization, User> capture)
    {
        capture((Organization)args[0]!, (User)args[1]!);
        return Task.FromResult(RegisterConflict.None);
    }
}
