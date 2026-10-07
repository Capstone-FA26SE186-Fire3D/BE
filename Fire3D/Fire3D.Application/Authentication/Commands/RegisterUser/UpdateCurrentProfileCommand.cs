using Fire3D.Application.Authentication.Commands.SelfRegistration;
using Fire3D.Application.Authentication.Internal;
using Fire3D.Domain.Enums;
using MediatR;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fire3D.Application.Authentication.Commands.RegisterUser;

/// <summary>Partial profile patch. The three optional profile fields retain whether JSON supplied them, including null.</summary>
[JsonConverter(typeof(UpdateCurrentProfileRequestJsonConverter))]
public sealed record UpdateCurrentProfileRequest
{
    public UpdateCurrentProfileRequest() { }
    public UpdateCurrentProfileRequest(string? fullName, string? username = null)
    {
        FullName = fullName;
        Username = username;
    }

    public string? FullName { get; set; }
    public string? Username { get; set; }

    private DateOnly? dob;
    public DateOnly? Dob
    {
        get => dob;
        set { dob = value; DobSpecified = true; }
    }
    [JsonIgnore] public bool DobSpecified { get; private set; }

    private UserGender? gender;
    public UserGender? Gender
    {
        get => gender;
        set { gender = value; GenderSpecified = true; }
    }
    [JsonIgnore] public bool GenderSpecified { get; private set; }

    private string? phoneNumber;
    public string? PhoneNumber
    {
        get => phoneNumber;
        set { phoneNumber = value; PhoneNumberSpecified = true; }
    }
    [JsonIgnore] public bool PhoneNumberSpecified { get; private set; }
}

public sealed class UpdateCurrentProfileRequestJsonConverter : JsonConverter<UpdateCurrentProfileRequest>
{
    public override UpdateCurrentProfileRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Profile request must be an object.");
        var request = new UpdateCurrentProfileRequest();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case "fullName": request.FullName = ReadString(property.Value, "fullName"); break;
                case "username": request.Username = ReadString(property.Value, "username"); break;
                case "dob": request.Dob = property.Value.ValueKind == JsonValueKind.Null ? null : ReadDateOnly(property.Value); break;
                case "gender": request.Gender = property.Value.ValueKind == JsonValueKind.Null ? null : ReadGender(property.Value); break;
                case "phoneNumber": request.PhoneNumber = ReadString(property.Value, "phoneNumber"); break;
            }
        }
        return request;
    }

    public override void Write(Utf8JsonWriter writer, UpdateCurrentProfileRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.FullName is not null) writer.WriteString("fullName", value.FullName);
        if (value.Username is not null) writer.WriteString("username", value.Username);
        if (value.DobSpecified) { writer.WritePropertyName("dob"); JsonSerializer.Serialize(writer, value.Dob, options); }
        if (value.GenderSpecified) { writer.WritePropertyName("gender"); JsonSerializer.Serialize(writer, value.Gender, options); }
        if (value.PhoneNumberSpecified) { writer.WritePropertyName("phoneNumber"); JsonSerializer.Serialize(writer, value.PhoneNumber, options); }
        writer.WriteEndObject();
    }

    private static string? ReadString(JsonElement value, string property) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        _ => throw new JsonException($"{property} must be a string or null.")
    };

    private static UserGender ReadGender(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric)) return (UserGender)numeric;
        if (value.ValueKind == JsonValueKind.String && Enum.TryParse<UserGender>(value.GetString(), true, out var parsed)) return parsed;
        throw new JsonException("gender must be a valid enum value or null.");
    }

    private static DateOnly ReadDateOnly(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", out var date)) return date;
        throw new JsonException("dob must be an ISO-8601 date (yyyy-MM-dd) or null.");
    }
}

public sealed record UpdateCurrentProfileCommand(Guid UserId, string? IfMatch, UpdateCurrentProfileRequest Request)
    : IRequest<AuthResult<AccountResponse>>;

public sealed class UpdateCurrentProfileCommandHandler(IAuthStore store, TimeProvider clock)
    : IRequestHandler<UpdateCurrentProfileCommand, AuthResult<AccountResponse>>
{
    public async Task<AuthResult<AccountResponse>> Handle(UpdateCurrentProfileCommand command, CancellationToken ct)
    {
        if (!ProfileEtag.TryParse(command.IfMatch, out var expectedRevision))
            return AuthResult<AccountResponse>.Fail(string.IsNullOrWhiteSpace(command.IfMatch) ? "PRECONDITION_REQUIRED" : "VALIDATION_ERROR",
                string.IsNullOrWhiteSpace(command.IfMatch) ? "Send the ETag from GET /api/auth/me in If-Match." : "If-Match must contain one quoted positive revision.",
                string.IsNullOrWhiteSpace(command.IfMatch) ? 428 : 400);

        if (command.UserId == Guid.Empty || command.Request is null)
            return AuthResult<AccountResponse>.Fail("VALIDATION_ERROR", "Profile request is invalid.", 400,
                new Dictionary<string, string[]> { ["request"] = ["Send at least one profile field."] });

        var name = command.Request.FullName?.Trim();
        var username = command.Request.Username is null ? null : SelfRegistrationValidation.NormalizeUsername(command.Request.Username);
        var phoneNumber = command.Request.PhoneNumberSpecified
            ? command.Request.PhoneNumber is null ? null : SelfRegistrationValidation.NormalizePhone(command.Request.PhoneNumber)
            : null;
        var errors = new Dictionary<string, string[]>();
        if (name is null && command.Request.Username is null && !command.Request.DobSpecified && !command.Request.GenderSpecified && !command.Request.PhoneNumberSpecified)
            errors["request"] = ["Send at least one profile field."];
        if (command.Request.FullName is not null && (string.IsNullOrWhiteSpace(name) || name.Length > 200))
            errors["fullName"] = ["Full name must not be empty and must be at most 200 characters."];
        if (command.Request.Username is not null && username is null)
            errors["username"] = ["Username phải dài 3–30 ký tự, chỉ gồm a-z, số, dấu chấm, gạch dưới hoặc gạch ngang."];
        if (command.Request.DobSpecified && command.Request.Dob is not null && command.Request.Dob > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            errors["dob"] = ["Date of birth cannot be in the future."];
        if (command.Request.GenderSpecified && command.Request.Gender is not null && !Enum.IsDefined(command.Request.Gender.Value))
            errors["gender"] = ["Gender is invalid."];
        if (command.Request.PhoneNumberSpecified && command.Request.PhoneNumber is not null && phoneNumber is null)
            errors["phoneNumber"] = ["Phone number must have 6-15 digits and may start with +."];
        if (errors.Count != 0)
            return AuthResult<AccountResponse>.Fail("VALIDATION_ERROR", "Profile request is invalid.", 400, errors);

        await using var transaction = await store.BeginUserTransactionAsync(command.UserId, ct);
        var user = await store.FindUserAsync(command.UserId, ct);
        if (user is null || !await AuthSupport.IsActiveAsync(store, user, ct))
            return AuthResult<AccountResponse>.Fail("UNAUTHORIZED", "Account is unavailable.", 401);

        var now = AuthSupport.UtcNow(clock);
        var result = await store.UpdateProfileAsync(user.Id, expectedRevision, name ?? user.FullName, username ?? user.Username,
            command.Request.DobSpecified ? command.Request.Dob : user.Dob,
            command.Request.GenderSpecified ? command.Request.Gender : user.Gender,
            command.Request.PhoneNumberSpecified ? phoneNumber : user.PhoneNumber, now, ct);
        if (result == ProfileUpdateResult.PreconditionFailed)
            return AuthResult<AccountResponse>.Fail("PRECONDITION_FAILED", "The profile changed. Reload it and retry.", 412);
        if (result == ProfileUpdateResult.UsernameTaken)
            return AuthResult<AccountResponse>.Fail("USERNAME_EXISTS", "Username is already registered.", 409);
        if (result == ProfileUpdateResult.PhoneTaken)
            return AuthResult<AccountResponse>.Fail("PHONE_NUMBER_EXISTS", "Số điện thoại cá nhân đã được sử dụng.", 409,
                new Dictionary<string, string[]> { ["phoneNumber"] = ["Chọn số điện thoại cá nhân khác."] });

        user.FullName = name ?? user.FullName;
        user.Username = username ?? user.Username;
        user.Dob = command.Request.DobSpecified ? command.Request.Dob : user.Dob;
        user.Gender = command.Request.GenderSpecified ? command.Request.Gender : user.Gender;
        user.PhoneNumber = command.Request.PhoneNumberSpecified ? phoneNumber : user.PhoneNumber;
        user.ProfileRevision = expectedRevision + 1;
        user.UpdatedAt = now;
        await store.WriteAuditAsync(user, "Update", user.Id, now, ct);
        await transaction.CommitAsync(ct);
        return AuthResult<AccountResponse>.Ok(AuthSupport.ToAccount(user));
    }
}
