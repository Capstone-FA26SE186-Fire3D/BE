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
            .Handle(new(admin.Id, Guid.Empty, new("Office", null, 1, null, null), "key-1"), default);
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
            .Handle(new(admin.Id, Guid.NewGuid(), request, "key-1"), default);
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
            .Handle(new(owner.Id, org, new("Office", null, 1, location, contact), "key-1"), default);
        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
        Assert.Contains(invalid == "email" ? "contact.email" : "location." + invalid, result.Error!.Errors!.Keys);
    }

    [Fact]
    public async Task Trainee_cannot_invoke_mutation_handler_directly()
    {
        var trainee = new User { Id = Guid.NewGuid(), Role = UserRole.Trainee, IsActive = true };
        var result = await Handler(Accounts(trainee))
            .Handle(new(trainee.Id, Guid.NewGuid(), new("Office", null, 1, null, null), "key-1"), default);
        Assert.Equal(403, result.Error?.Status);
    }

    [Fact]
    public async Task Organization_user_cannot_select_another_tenant_even_when_calling_handler_directly()
    {
        var owner = new User { Id = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), Role = UserRole.OrganizationUser, IsActive = true };
        var result = await Handler(Accounts(owner)).Handle(new(owner.Id, Guid.NewGuid(), new("Office", null, 1, null, null), "key-1"), default);
        Assert.Equal(403, result.Error?.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    public async Task Create_requires_a_printable_idempotency_key_before_any_lookup(string? key)
    {
        var accounts = ResetProxy.For<IAuthStore>((method, _) => throw new InvalidOperationException("Unexpected lookup: " + method));
        var result = await Handler(accounts).Handle(new(Guid.NewGuid(), null, new("Office", null, 1, null, null), key), default);
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", result.Error?.Code);
        Assert.Equal(400, result.Error?.Status);
    }

    // totalFloors=0 keeps every case a validation error, so only the geojson field decides whether it is reported.
    [Theory]
    [InlineData("""{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[105.8,21.0],[105.9,21.0],[105.9,21.1],[105.8,21.0]]]}}""", true)]
    [InlineData("""{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"MultiPoint","coordinates":[[105.8,21.0]]}}]}""", true)]
    [InlineData("""{"type":"GeometryCollection","geometries":[{"type":"LineString","coordinates":[[0,0],[1,1]]},{"type":"MultiLineString","coordinates":[[[0,0],[1,1]]]},{"type":"MultiPolygon","coordinates":[[[[0,0],[1,0],[1,1],[0,0]]]]}]}""", true)]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,1]]]}""", false)]
    [InlineData("""{"type":"GeometryCollection","geometries":{}}""", false)]
    [InlineData("""{"type":"LineString"}""", false)]
    [InlineData("""{"type":"Circle","coordinates":[0,0]}""", false)]
    [InlineData("""{"type":"MultiPolygon","coordinates":[]}""", false)]
    [InlineData("{not json", false)]
    public async Task Geojson_accepts_closed_wgs84_geometries_and_collections_only(string geojson, bool valid)
    {
        var org = Guid.NewGuid();
        var owner = new User { Id = Guid.NewGuid(), OrganizationId = org, Role = UserRole.OrganizationUser, IsActive = true };
        var location = new CreateBuildingLocationRequest("Address", "Hanoi", null, 21.0m, 105.8m, geojson);
        var result = await Handler(Accounts(owner)).Handle(new(owner.Id, org, new("Office", null, 0, location, null), "key-1"), default);
        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
        Assert.Contains("totalFloors", result.Error!.Errors!.Keys);
        Assert.Equal(!valid, result.Error.Errors.ContainsKey("location.geojson"));
    }

    [Fact]
    public async Task Text_fields_report_length_and_control_characters_per_field()
    {
        var org = Guid.NewGuid();
        var owner = new User { Id = Guid.NewGuid(), OrganizationId = org, Role = UserRole.OrganizationUser, IsActive = true };
        var contact = new CreateBuildingContactRequest(" ", new string('r', 101), "090\u0007", null, true);
        var location = new CreateBuildingLocationRequest(null, new string('c', 256), "Ba\tDinh", null, null, null);
        var result = await Handler(Accounts(owner)).Handle(new(owner.Id, org, new("Off\u0001ice", new string('t', 101), 1, location, contact), "key-1"), default);
        Assert.Equal(["buildingType", "contact.contactName", "contact.contactRole", "contact.phone", "location.city", "location.district", "name"],
            result.Error!.Errors!.Keys.Order());
        Assert.Equal("contact.contactName is required and must contain at most 255 characters without control characters.", result.Error.Errors["contact.contactName"].Single());
    }

    [Fact]
    public void Create_hash_binds_the_resolved_organization_and_normalized_input()
    {
        var org = Guid.NewGuid();
        Assert.Equal(BuildingCommandKey.Hash(org, new(" Office ", null, 1, null, null)), BuildingCommandKey.Hash(org, new("Office", null, 1, null, null)));
        Assert.NotEqual(BuildingCommandKey.Hash(org, new("Office", null, 1, null, null)), BuildingCommandKey.Hash(Guid.NewGuid(), new("Office", null, 1, null, null)));
        Assert.Matches("^[0-9a-f]{64}$", BuildingCommandKey.Hash(org, new("Office", null, 2, null, null)));
    }

    [Fact]
    public void Building_etag_tracks_postgres_microseconds_and_always_moves_forward()
    {
        var stored = new DateTime(2026, 10, 14, 9, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
        Assert.True(BuildingEtag.TryParse(BuildingEtag.Format(stored), out var version));
        Assert.Equal(BuildingEtag.Version(stored), version);
        Assert.False(BuildingEtag.TryParse("123", out _));
        Assert.False(BuildingEtag.TryParse("W/\"123\"", out _));
        Assert.False(BuildingEtag.TryParse("\"0\"", out _));
        var next = BuildingEtag.Next(stored.AddTicks(-50), stored);
        Assert.True(BuildingEtag.Version(next) > BuildingEtag.Version(stored));
        Assert.Equal(0, BuildingEtag.Next(stored.AddSeconds(1).AddTicks(7), stored).Ticks % 10);
    }
}

