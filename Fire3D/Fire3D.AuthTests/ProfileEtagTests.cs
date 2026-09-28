using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.RegisterUser;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class ProfileEtagTests
{
    [Fact]
    public async Task Patch_profile_requires_the_get_etag_and_advances_its_revision()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "profile@example.test", FullName = "Before", Username = "before", Role = UserRole.Trainee, IsActive = true, ProfileRevision = 4 };
        var committed = false;
        var transaction = ResetProxy.For<IAuthTransaction>((method, _) => method switch
        {
            "CommitAsync" => Task.Run(() => { committed = true; }),
            "DisposeAsync" => ValueTask.CompletedTask,
            _ => throw new InvalidOperationException(method)
        });
        var store = ResetProxy.For<IAuthStore>((method, args) => method switch
        {
            "BeginUserTransactionAsync" => Task.FromResult(transaction),
            "FindUserAsync" => Task.FromResult<User?>(user),
            "UpdateProfileAsync" => AssertUpdate(args),
            "WriteAuditAsync" => Task.CompletedTask,
            _ => throw new InvalidOperationException("Unexpected store operation: " + method)
        });

        var result = await new UpdateCurrentProfileCommandHandler(store, TimeProvider.System)
            .Handle(new(user.Id, "\"4\"", new UpdateCurrentProfileRequest("After", "After.User")), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("after.user", result.Value!.Username);
        Assert.Equal(5, result.Value.ProfileRevision);
        Assert.True(committed);
    }

    [Fact]
    public async Task Patch_profile_rejects_missing_etag_before_accessing_the_store()
    {
        var store = ResetProxy.For<IAuthStore>((method, _) => throw new InvalidOperationException("Unexpected store operation: " + method));
        var result = await new UpdateCurrentProfileCommandHandler(store, TimeProvider.System)
            .Handle(new(Guid.NewGuid(), null, new UpdateCurrentProfileRequest("After")), default);

        Assert.Equal("PRECONDITION_REQUIRED", result.Error?.Code);
        Assert.Equal(428, result.Error?.Status);
    }

    [Fact]
    public async Task Patch_profile_reports_invalid_username_by_field_before_accessing_the_store()
    {
        var store = ResetProxy.For<IAuthStore>((method, _) => throw new InvalidOperationException("Unexpected store operation: " + method));
        var result = await new UpdateCurrentProfileCommandHandler(store, TimeProvider.System)
            .Handle(new(Guid.NewGuid(), "\"1\"", new UpdateCurrentProfileRequest(null, "bad username")), default);

        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
        Assert.Equal("Username phải dài 3–30 ký tự, chỉ gồm a-z, số, dấu chấm, gạch dưới hoặc gạch ngang.", result.Error?.Errors?["username"][0]);
    }

    private static Task<ProfileUpdateResult> AssertUpdate(object?[] args)
    {
        Assert.Equal(4L, args[1]);
        Assert.Equal("After", args[2]);
        Assert.Equal("after.user", args[3]);
        return Task.FromResult(ProfileUpdateResult.Updated);
    }
}
