using Fire3D.Domain.Enums;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fire3D.Application.Administration;

public sealed record CreateOrganizationRequest(string Name, string Slug);
public sealed record SetActiveRequest(bool? IsActive);
public sealed record OrganizationResponse(Guid Id, string Name, string Slug, bool IsActive,
    DateTime CreatedAt, DateTime UpdatedAt);
public sealed record OrganizationProfileResponse(Guid Id, string Name, string Slug, string? Address, string? PhoneNumber,
    bool IsActive, long ProfileRevision, DateTime CreatedAt, DateTime UpdatedAt);
[JsonConverter(typeof(UpdateOrganizationProfileRequestJsonConverter))]
public sealed class UpdateOrganizationProfileRequest
{
    public UpdateOrganizationProfileRequest() { }
    public UpdateOrganizationProfileRequest(string? name, string? address, string? phoneNumber)
    {
        Name = name; Address = address; PhoneNumber = phoneNumber;
        NameSpecified = AddressSpecified = PhoneNumberSpecified = true;
    }
    public string? Name { get; set; }
    public string? Address { get; set; }
    public string? PhoneNumber { get; set; }
    [JsonIgnore] public bool NameSpecified { get; private set; }
    [JsonIgnore] public bool AddressSpecified { get; private set; }
    [JsonIgnore] public bool PhoneNumberSpecified { get; private set; }
    internal void SetName(string? value) { Name = value; NameSpecified = true; }
    internal void SetAddress(string? value) { Address = value; AddressSpecified = true; }
    internal void SetPhoneNumber(string? value) { PhoneNumber = value; PhoneNumberSpecified = true; }
}

public sealed class UpdateOrganizationProfileRequestJsonConverter : JsonConverter<UpdateOrganizationProfileRequest>
{
    public override UpdateOrganizationProfileRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Organization profile request must be an object.");
        var request = new UpdateOrganizationProfileRequest();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => property.Value.GetString(),
                _ => throw new JsonException($"{property.Name} must be a string or null.")
            };
            switch (property.Name)
            {
                case "name": request.SetName(value); break;
                case "address": request.SetAddress(value); break;
                case "phoneNumber": request.SetPhoneNumber(value); break;
            }
        }
        return request;
    }
    public override void Write(Utf8JsonWriter writer, UpdateOrganizationProfileRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.NameSpecified) writer.WriteString("name", value.Name);
        if (value.AddressSpecified) writer.WriteString("address", value.Address);
        if (value.PhoneNumberSpecified) writer.WriteString("phoneNumber", value.PhoneNumber);
        writer.WriteEndObject();
    }
}
public enum OrganizationProfileUpdateResult { Updated, PreconditionFailed, Unavailable }
public sealed record ManagedAccountResponse(Guid Id, string Email, string? FullName, UserRole Role,
    Guid? OrganizationId, bool IsActive, DateTime? LastLoginAt, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record PageResponse<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public sealed record OrganizationFilter(int Page = 1, int PageSize = 20, string? Search = null, bool? IsActive = null);
public sealed record AccountFilter(int Page = 1, int PageSize = 20, string? Search = null,
    bool? IsActive = null, UserRole? Role = null, Guid? OrganizationId = null);
