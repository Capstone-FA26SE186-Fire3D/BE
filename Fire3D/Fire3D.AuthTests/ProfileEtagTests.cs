using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.RegisterUser;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using System.Text.Json;
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

    [Fact]
    public async Task Patch_profile_rejects_a_whitespace_only_name_before_accessing_the_store()
    {
        var store = ResetProxy.For<IAuthStore>((method, _) => throw new InvalidOperationException("Unexpected store operation: " + method));
        var result = await new UpdateCurrentProfileCommandHandler(store, TimeProvider.System)
            .Handle(new(Guid.NewGuid(), "\"1\"", new UpdateCurrentProfileRequest("   ")), default);

        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
        Assert.Contains("fullName", result.Error?.Errors?.Keys ?? []);
    }

    [Fact]
    public async Task Patch_profile_updates_optional_personal_fields_and_keeps_omitted_fields()
    {
        var user = new User { Id = Guid.NewGuid(), Email = "profile@example.test", FullName = "Before", Username = "before", Role = UserRole.Trainee, IsActive = true, ProfileRevision = 4, Dob = new DateOnly(2000, 1, 1), Gender = UserGender.Male, PhoneNumber = "+84123456789" };
        var request = new UpdateCurrentProfileRequest() { Dob = null, Gender = UserGender.Female, PhoneNumber = " 0987654321 " };
        var transaction = ResetProxy.For<IAuthTransaction>((method, _) => method switch { "CommitAsync" => Task.CompletedTask, "DisposeAsync" => ValueTask.CompletedTask, _ => throw new InvalidOperationException(method) });
        var store = ResetProxy.For<IAuthStore>((method, args) => method switch
        {
            "BeginUserTransactionAsync" => Task.FromResult(transaction),
            "FindUserAsync" => Task.FromResult<User?>(user),
            "UpdateProfileAsync" => AssertPersonalUpdate(args),
            "WriteAuditAsync" => Task.CompletedTask,
            _ => throw new InvalidOperationException("Unexpected store operation: " + method)
        });

        var result = await new UpdateCurrentProfileCommandHandler(store, TimeProvider.System).Handle(new(user.Id, "\"4\"", request), default);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Dob);
        Assert.Equal(UserGender.Female, result.Value.Gender);
        Assert.Equal("0987654321", result.Value.PhoneNumber);
    }

    [Fact]
    public void Patch_profile_distinguishes_omitted_optional_fields_from_explicit_null()
    {
        var omitted = JsonSerializer.Deserialize<UpdateCurrentProfileRequest>("{}");
        var cleared = JsonSerializer.Deserialize<UpdateCurrentProfileRequest>("{\"dob\":null,\"gender\":null,\"phoneNumber\":null}");

        Assert.False(omitted!.DobSpecified);
        Assert.False(omitted.GenderSpecified);
        Assert.False(omitted.PhoneNumberSpecified);
        Assert.True(cleared!.DobSpecified);
        Assert.True(cleared.GenderSpecified);
        Assert.True(cleared.PhoneNumberSpecified);
    }

    private static Task<ProfileUpdateResult> AssertUpdate(object?[] args)
    {
        Assert.Equal(4L, args[1]);
        Assert.Equal("After", args[2]);
        Assert.Equal("after.user", args[3]);
        return Task.FromResult(ProfileUpdateResult.Updated);
    }

    private static Task<ProfileUpdateResult> AssertPersonalUpdate(object?[] args)
    {
        Assert.Equal(4L, args[1]);
        Assert.Equal("Before", args[2]);
        Assert.Equal("before", args[3]);
        Assert.Null(args[4]);
        Assert.Equal(UserGender.Female, args[5]);
        Assert.Equal("0987654321", args[6]);
        return Task.FromResult(ProfileUpdateResult.Updated);
    }
}
