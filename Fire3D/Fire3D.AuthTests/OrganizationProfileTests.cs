using Fire3D.Application.Administration;
using Fire3D.Application.Administration.Commands.UpdateMyOrganization;
using Fire3D.Application.Authentication;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using System.Text.Json;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class OrganizationProfileTests
{
    [Fact]
    public async Task Organization_user_updates_only_own_profile_with_etag()
    {
        var id = Guid.NewGuid();
        var actor = new User { Id = Guid.NewGuid(), OrganizationId = id, Role = UserRole.OrganizationUser, IsActive = true };
        var organization = new Organization { Id = id, Name = "Before", Slug = "before", IsActive = true, ProfileRevision = 2, CreatedAt = DateTime.UtcNow };
        var tx = ResetProxy.For<IAuthTransaction>((method, _) => method switch { "CommitAsync" => Task.CompletedTask, "DisposeAsync" => ValueTask.CompletedTask, _ => throw new InvalidOperationException(method) });
        var accounts = ResetProxy.For<IAuthStore>((method, _) => method switch { "FindUserAsync" => Task.FromResult<User?>(actor), "OrganizationIsActiveAsync" => Task.FromResult(true), _ => throw new InvalidOperationException(method) });
        var store = ResetProxy.For<IAdministrationStore>((method, args) => method switch
        {
            "BeginManagementTransactionAsync" => Task.FromResult(tx),
            "FindOrganizationAsync" => Task.FromResult<Organization?>(organization),
            "UpdateOrganizationProfileAsync" => Task.FromResult(OrganizationProfileUpdateResult.Updated),
            "WriteAuditAsync" => Task.CompletedTask,
            _ => throw new InvalidOperationException(method)
        });

        var result = await new UpdateMyOrganizationCommandHandler(accounts, store, TimeProvider.System)
            .Handle(new(actor.Id, "\"2\"", new("After", "Address", "+84123456789")), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("After", result.Value!.Name);
        Assert.Equal(3, result.Value.ProfileRevision);
    }

    [Fact]
    public async Task Organization_patch_preserves_fields_omitted_from_json()
    {
        var id = Guid.NewGuid();
        var actor = new User { Id = Guid.NewGuid(), OrganizationId = id, Role = UserRole.OrganizationUser, IsActive = true };
        var organization = new Organization { Id = id, Name = "Before", Slug = "before", Address = "Old address", PhoneNumber = "+84123456789", IsActive = true, ProfileRevision = 2, CreatedAt = DateTime.UtcNow };
        string? savedName = null, savedAddress = null, savedPhone = null;
        var tx = ResetProxy.For<IAuthTransaction>((method, _) => method switch { "CommitAsync" => Task.CompletedTask, "DisposeAsync" => ValueTask.CompletedTask, _ => throw new InvalidOperationException(method) });
        var accounts = ResetProxy.For<IAuthStore>((method, _) => method switch { "FindUserAsync" => Task.FromResult<User?>(actor), "OrganizationIsActiveAsync" => Task.FromResult(true), _ => throw new InvalidOperationException(method) });
        var store = ResetProxy.For<IAdministrationStore>((method, args) => method switch
        {
            "BeginManagementTransactionAsync" => Task.FromResult(tx),
            "FindOrganizationAsync" => Task.FromResult<Organization?>(organization),
            "UpdateOrganizationProfileAsync" => CaptureUpdate(args!, out savedName, out savedAddress, out savedPhone),
            "WriteAuditAsync" => Task.CompletedTask,
            _ => throw new InvalidOperationException(method)
        });
        var request = JsonSerializer.Deserialize<UpdateOrganizationProfileRequest>("{\"address\":\"New address\"}")!;

        var result = await new UpdateMyOrganizationCommandHandler(accounts, store, TimeProvider.System)
            .Handle(new(actor.Id, "\"2\"", request), default);

        Assert.True(result.IsSuccess);
        Assert.Equal("Before", savedName);
        Assert.Equal("New address", savedAddress);
        Assert.Equal("+84123456789", savedPhone);
    }

    private static Task<OrganizationProfileUpdateResult> CaptureUpdate(object?[] args, out string? name, out string? address, out string? phone)
    {
        name = (string?)args[2]; address = (string?)args[3]; phone = (string?)args[4];
        return Task.FromResult(OrganizationProfileUpdateResult.Updated);
    }
}
