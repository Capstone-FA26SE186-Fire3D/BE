using System.Text.Json;
using System.Text.Json.Serialization;
using Fire3D.Application.Administration;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Commands.RegisterUser;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class ProfileRequestConverterTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static UpdateCurrentProfileRequest Read(string json) => JsonSerializer.Deserialize<UpdateCurrentProfileRequest>(json, Web)!;

    [Fact]
    public void Personal_profile_reader_keeps_explicit_nulls_and_accepts_numeric_or_named_gender()
    {
        var named = Read("""{"fullName":"A","username":"user.one","dob":null,"gender":"female","phoneNumber":null,"ignored":1}""");
        Assert.Equal(("A", "user.one"), (named.FullName, named.Username));
        Assert.True(named.DobSpecified && named.GenderSpecified && named.PhoneNumberSpecified);
        Assert.Equal(UserGender.Female, named.Gender);
        Assert.Null(named.Dob);
        var numeric = Read($$"""{"gender":{{(int)UserGender.Male}},"dob":"2001-02-03"}""");
        Assert.Equal(UserGender.Male, numeric.Gender);
        Assert.Equal(new DateOnly(2001, 2, 3), numeric.Dob);
        Assert.False(numeric.PhoneNumberSpecified);
        var nulls = Read("""{"username":null,"gender":null}""");
        Assert.Null(nulls.Username);
        Assert.True(nulls.GenderSpecified);
        Assert.Null(nulls.Gender);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"fullName":1}""")]
    [InlineData("""{"username":true}""")]
    [InlineData("""{"phoneNumber":{}}""")]
    [InlineData("""{"gender":"robot"}""")]
    [InlineData("""{"gender":1.5}""")]
    [InlineData("""{"gender":[]}""")]
    [InlineData("""{"dob":"03/02/2001"}""")]
    [InlineData("""{"dob":20010203}""")]
    public void Personal_profile_reader_rejects_wrongly_typed_fields(string json) =>
        Assert.Throws<JsonException>(() => Read(json));

    [Fact]
    public void Personal_profile_writer_emits_only_supplied_fields_including_explicit_nulls()
    {
        Assert.Equal("{}", JsonSerializer.Serialize(new UpdateCurrentProfileRequest(), Web));
        var request = new UpdateCurrentProfileRequest("Name", "user") { Dob = new DateOnly(2000, 1, 2), Gender = null, PhoneNumber = null };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(request, Web));
        var root = json.RootElement;
        Assert.Equal(["fullName", "username", "dob", "gender", "phoneNumber"], root.EnumerateObject().Select(x => x.Name));
        Assert.Equal("2000-01-02", root.GetProperty("dob").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("gender").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("phoneNumber").ValueKind);
        var roundTrip = Read(json.RootElement.GetRawText());
        Assert.True(roundTrip.GenderSpecified && roundTrip.PhoneNumberSpecified);
        Assert.Equal(request.Dob, roundTrip.Dob);
        Assert.Equal("""{"gender":"Male"}""", JsonSerializer.Serialize(new UpdateCurrentProfileRequest { Gender = UserGender.Male }, Web));
    }

    [Fact]
    public void Organization_profile_converter_rejects_non_strings_and_writes_only_supplied_fields()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpdateOrganizationProfileRequest>("""{"name":5}""", Web));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpdateOrganizationProfileRequest>("""{"address":false}""", Web));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpdateOrganizationProfileRequest>("\"name\"", Web));
        Assert.Equal("{}", JsonSerializer.Serialize(new UpdateOrganizationProfileRequest(), Web));
        Assert.Equal("""{"name":"Org","address":null,"phoneNumber":"0901"}""",
            JsonSerializer.Serialize(new UpdateOrganizationProfileRequest("Org", null, "0901"), Web));
        var partial = JsonSerializer.Deserialize<UpdateOrganizationProfileRequest>("""{"address":"1 Street"}""", Web)!;
        Assert.Equal("""{"address":"1 Street"}""", JsonSerializer.Serialize(partial, Web));
    }

    [Theory]
    [InlineData(UserRole.Trainee, "\"trainee\"")]
    [InlineData(UserRole.OrganizationUser, "\"organization\"")]
    [InlineData(UserRole.PlatformAdmin, "\"invalid\"")]
    public void Google_account_type_writer_never_emits_an_authorization_role_name(UserRole role, string json) =>
        Assert.Equal(json, JsonSerializer.Serialize(role, new JsonSerializerOptions { Converters = { new GoogleAccountTypeJsonConverter() } }));

    private static readonly Guid UserId =Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static UpdateCurrentProfileCommandHandler Handler(IAuthStore store) =>
        new(store, new FixedClock(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero)));
    private static IAuthStore Unexpected() => ResetProxy.For<IAuthStore>((method, _) => throw new InvalidOperationException("Unexpected: " + method));

    [Fact]
    public async Task Profile_update_rejects_empty_identity_null_request_and_empty_patch_before_the_store()
    {
        foreach (var command in new[]
        {
            new UpdateCurrentProfileCommand(Guid.Empty, "\"1\"", new("Name")),
            new UpdateCurrentProfileCommand(UserId, "\"1\"", null!),
            new UpdateCurrentProfileCommand(UserId, "\"1\"", new UpdateCurrentProfileRequest())
        })
        {
            var result = await Handler(Unexpected()).Handle(command, default);
            Assert.Equal(("VALIDATION_ERROR", 400), (result.Error?.Code, result.Error?.Status));
            Assert.True(result.Error!.Errors!.ContainsKey("request"));
        }
    }

    [Fact]
    public async Task Profile_update_reports_future_dob_and_undefined_gender_as_field_errors()
    {
        var request = new UpdateCurrentProfileRequest { Dob = new DateOnly(2026, 10, 21), Gender = (UserGender)99 };
        var result = await Handler(Unexpected()).Handle(new(UserId, "\"1\"", request), default);
        Assert.Equal(400, result.Error?.Status);
        Assert.Equal(["dob", "gender"], result.Error!.Errors!.Keys.Order());
        var today = await Handler(Store(null, ProfileUpdateResult.Updated)).Handle(new(UserId, "\"1\"", new UpdateCurrentProfileRequest { Dob = new DateOnly(2026, 10, 20) }), default);
        Assert.Equal(("UNAUTHORIZED", 401), (today.Error?.Code, today.Error?.Status));
    }

    [Fact]
    public async Task Profile_update_is_unauthorized_for_missing_or_inactive_accounts()
    {
        var inactive = new User { Id = UserId, Email = "a@example.test", Role = UserRole.Trainee, IsActive = false };
        foreach (var user in new[] { null, inactive })
        {
            var result = await Handler(Store(user, ProfileUpdateResult.Updated)).Handle(new(UserId, "\"1\"", new("Name")), default);
            Assert.Equal(("UNAUTHORIZED", 401), (result.Error?.Code, result.Error?.Status));
        }
    }

    [Fact]
    public async Task Profile_update_maps_a_username_conflict_to_409()
    {
        var user = new User { Id = UserId, Email = "a@example.test", Role = UserRole.Trainee, IsActive = true };
        var result = await Handler(Store(user, ProfileUpdateResult.UsernameTaken)).Handle(new(UserId, "\"1\"", new(null, "taken.name")), default);
        Assert.Equal(("USERNAME_EXISTS", 409), (result.Error?.Code, result.Error?.Status));
    }

    private static IAuthStore Store(User? user, ProfileUpdateResult update)
    {
        var transaction = ResetProxy.For<IAuthTransaction>((method, _) => method switch
        {
            "DisposeAsync" => ValueTask.CompletedTask,
            _ => throw new InvalidOperationException("A rejected update must not commit.")
        });
        return ResetProxy.For<IAuthStore>((method, _) => method switch
        {
            "BeginUserTransactionAsync" => Task.FromResult(transaction),
            "FindUserAsync" => Task.FromResult(user),
            "UpdateProfileAsync" => Task.FromResult(update),
            _ => throw new InvalidOperationException("Unexpected: " + method)
        });
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
