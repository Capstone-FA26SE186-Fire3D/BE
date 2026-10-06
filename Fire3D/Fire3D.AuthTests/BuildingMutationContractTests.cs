using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Buildings;
using Fire3D.Application.Buildings.Commands.CreateBuilding;
using Fire3D.Application.Buildings.Commands.UpdateBuilding;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Xunit;
using MediatR;

namespace Fire3D.AuthTests;

public sealed class BuildingMutationContractTests
{
    private static IAuthStore Accounts(User actor) => ResetProxy.For<IAuthStore>((method, _) => method switch
    {
        "FindUserAsync" => Task.FromResult<User?>(actor),
        "OrganizationIsActiveAsync" => Task.FromResult(true),
        _ => throw new InvalidOperationException(method)
    });
    private static IRequestHandler<CreateBuildingCommand, AuthResult<BuildingResponse>> Handler(IAuthStore accounts) =>
        (IRequestHandler<CreateBuildingCommand, AuthResult<BuildingResponse>>)Activator.CreateInstance(typeof(CreateBuildingCommand).Assembly.GetType("Fire3D.Application.Buildings.Commands.CreateBuilding.CreateBuildingCommandHandler")!, NoWrites(), accounts, TimeProvider.System)!;
    private static IBuildingStore NoWrites() => ResetProxy.For<IBuildingStore>((method, _) => throw new InvalidOperationException("Unexpected persistence: " + method));

    [Fact]
    public async Task Admin_create_requires_explicit_target_organization_with_field_error()
    {
        var admin = new User { Id = Guid.NewGuid(), Role = UserRole.PlatformAdmin, IsActive = true };
        var result = await Handler(Accounts(admin))
            .Handle(new(admin.Id, Guid.Empty, new("Office", null, 1, null, null)), default);
        Assert.Equal(400, result.Error?.Status);
        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
        Assert.Contains("organizationId", result.Error!.Errors!.Keys);
    }

    [Fact]
    public async Task Body_target_is_supported_and_conflicting_query_target_is_rejected()
    {
        var target = Guid.NewGuid();
        var request = JsonSerializer.Deserialize<CreateBuildingRequest>($$"""{"name":"Office","totalFloors":1,"organizationId":"{{target}}"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var admin = new User { Id = Guid.NewGuid(), Role = UserRole.PlatformAdmin, IsActive = true };
        var result = await Handler(Accounts(admin))
            .Handle(new(admin.Id, Guid.NewGuid(), request), default);
        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
        Assert.Contains("organizationId", result.Error!.Errors!.Keys);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("latitude")]
    [InlineData("longitude")]
    [InlineData("geojson")]
    public async Task Invalid_nested_input_returns_precise_field_error_without_writing(string invalid)
    {
        var org = Guid.NewGuid();
        var owner = new User { Id = Guid.NewGuid(), OrganizationId = org, Role = UserRole.OrganizationUser, IsActive = true };
        var location = new CreateBuildingLocationRequest("Address", "City", null,
            invalid == "latitude" ? 91 : 10, invalid == "longitude" ? 181 : 20,
            invalid == "geojson" ? "{\"type\":\"Point\",\"coordinates\":[181,10]}" : null);
        var contact = new CreateBuildingContactRequest("Owner", null, null, invalid == "email" ? "not-email" : "owner@example.test", true);
        var result = await Handler(Accounts(owner))
            .Handle(new(owner.Id, org, new("Office", null, 1, location, contact)), default);
        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
        Assert.Contains(invalid == "email" ? "contact.email" : "location." + invalid, result.Error!.Errors!.Keys);
    }

    [Fact]
    public async Task Trainee_cannot_invoke_mutation_handler_directly()
    {
        var trainee = new User { Id = Guid.NewGuid(), Role = UserRole.Trainee, IsActive = true };
        var result = await Handler(Accounts(trainee))
            .Handle(new(trainee.Id, Guid.NewGuid(), new("Office", null, 1, null, null)), default);
        Assert.Equal(403, result.Error?.Status);
    }

    [Fact]
    public async Task Organization_user_cannot_select_another_tenant_even_when_calling_handler_directly()
    {
        var owner = new User { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), Role = UserRole.OrganizationUser, IsActive = true };
        var result = await Handler(Accounts(owner)).Handle(new(owner.Id, Guid.NewGuid(), new("Office", null, 1, null, null)), default);
        Assert.Equal(403, result.Error?.Status);
    }
}

