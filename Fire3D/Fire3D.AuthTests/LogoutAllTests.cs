using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.Logout;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class LogoutAllTests
{
    [Fact]
    public async Task Logout_all_revokes_sessions_disables_push_and_writes_audit_in_one_transaction()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "logout@example.test", Role = UserRole.Trainee, IsActive = true };
        var calls = new List<string>();
        var transaction = ResetProxy.For<IAuthTransaction>((method, _) => method switch
        {
            "CommitAsync" => Task.Run(() => calls.Add("commit")),
            "DisposeAsync" => ValueTask.CompletedTask,
            _ => throw new InvalidOperationException(method)
        });
        var store = ResetProxy.For<IAuthStore>((method, _) => method switch
        {
            "BeginUserTransactionAsync" => Task.FromResult(transaction),
            "FindUserAsync" => Task.FromResult<User?>(user),
            "RevokeAllUserSessionsAsync" => Task.Run(() => calls.Add("sessions")),
            "DisableUserPushDevicesAsync" => Task.Run(() => calls.Add("devices")),
            "WriteAuditAsync" => Task.Run(() => calls.Add("audit")),
            _ => throw new InvalidOperationException("Unexpected store operation: " + method)
        });

        var result = await new LogoutAllCommandHandler(store, TimeProvider.System).Handle(new(user.Id), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(["sessions", "devices", "audit", "commit"], calls);
    }
}
