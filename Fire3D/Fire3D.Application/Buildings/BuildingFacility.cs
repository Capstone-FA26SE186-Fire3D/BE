using System.Text.Json;
using System.Text.RegularExpressions;
using Fire3D.Domain.Entities;

namespace Fire3D.Application.Buildings;

/// <summary>Lực lượng PCCC tại chỗ (Mẫu PC06 phần II): team size, leader and whether it is available in and outside working hours.</summary>
public sealed record OnSiteForce(int TeamSize, string? LeaderName, string? LeaderPhone, bool AvailableInHours, bool AvailableOffHours);
/// <summary>An emergency contact of the facility (e.g. the local fire police station).</summary>
public sealed record EmergencyContact(string Name, string Phone, string? Role = null);

/// <summary>BE#212: the two building types of Flow 1 PC06.</summary>
public static class BuildingTypes
{
    public const string Apartment = "Apartment";
    public const string School = "School";
    public static readonly string[] All = [Apartment, School];
}

/// <summary>
/// Risk group of Phụ lục II Nghị định 105/2025/NĐ-CP, computed from type, storeys and total floor area; null without a type.
/// Apartment: Group1 from 7 storeys or 3,000 m², Group2 from 5 storeys or 1,000 m². School: Group1 from 5 storeys or 3,000 m²,
/// Group2 from 3 storeys or 1,500 m². Below both: BelowThreshold.
/// </summary>
public static class BuildingRiskGroup
{
    public const string Group1 = "Group1", Group2 = "Group2", BelowThreshold = "BelowThreshold";

    public static string? Compute(string? buildingType, int totalFloors, decimal? floorAreaSquareMeters) => buildingType switch
    {
        BuildingTypes.Apartment => totalFloors >= 7 || floorAreaSquareMeters >= 3000m ? Group1
            : totalFloors >= 5 || floorAreaSquareMeters >= 1000m ? Group2 : BelowThreshold,
        BuildingTypes.School => totalFloors >= 5 || floorAreaSquareMeters >= 3000m ? Group1
            : totalFloors >= 3 || floorAreaSquareMeters >= 1500m ? Group2 : BelowThreshold,
        _ => null
    };
}

/// <summary>Validation, storage and read-back of the PC06 facility fields of a Building.</summary>
public static partial class BuildingFacility
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxEmergencyContacts = 20;

    [GeneratedRegex(@"^[0-9+() .\-]{3,32}$")]
    private static partial Regex PhonePattern();

    public static void Validate(Dictionary<string, string[]> errors, decimal? floorArea, string? headName, string? headPhone,
        OnSiteForce? force, IReadOnlyList<EmergencyContact>? contacts)
    {
        void Text(string field, string? value, int max, bool required = false)
        {
            if ((required && string.IsNullOrWhiteSpace(value)) || value?.Trim().Length > max || value?.Any(char.IsControl) == true)
                errors[field] = [$"{field} {(required ? "is required and " : "")}must contain at most {max} characters without control characters."];
        }
        void Phone(string field, string? value, bool required = false)
        {
            if (value is null) { if (required) errors[field] = [$"{field} is required."]; return; }
            if (!PhonePattern().IsMatch(value.Trim()) || value.Count(char.IsAsciiDigit) < 3)
                errors[field] = [$"{field} must be 3-32 characters of digits, spaces, +, -, (, ) or . with at least 3 digits (114 is valid)."];
        }
        if (floorArea is <= 0 or > 10_000_000m || (floorArea is decimal area && decimal.Round(area, 2) != area))
            errors["floorAreaSquareMeters"] = ["floorAreaSquareMeters must be greater than 0, at most 10,000,000, with at most 2 decimal places."];
        Text("facilityHeadName", headName, 200);
        Phone("facilityHeadPhone", headPhone);
        if (force is not null)
        {
            if (force.TeamSize is < 0 or > 10_000) errors["onSiteForce.teamSize"] = ["teamSize must be 0-10000."];
            Text("onSiteForce.leaderName", force.LeaderName, 200);
            Phone("onSiteForce.leaderPhone", force.LeaderPhone);
        }
        if (contacts is not null)
        {
            if (contacts.Count > MaxEmergencyContacts) errors["emergencyContacts"] = [$"At most {MaxEmergencyContacts} emergency contacts."];
            for (var i = 0; i < Math.Min(contacts.Count, MaxEmergencyContacts); i++)
            {
                if (contacts[i] is null) { errors[$"emergencyContacts[{i}]"] = ["A contact needs name and phone."]; continue; }
                Text($"emergencyContacts[{i}].name", contacts[i].Name, 200, true);
                Text($"emergencyContacts[{i}].role", contacts[i].Role, 100);
                Phone($"emergencyContacts[{i}].phone", contacts[i].Phone, true);
            }
        }
    }

    public static void Apply(Building building, decimal? floorArea, string? headName, string? headPhone, OnSiteForce? force, IReadOnlyList<EmergencyContact>? contacts)
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        building.FloorAreaSquareMeters = floorArea;
        building.FacilityHeadName = Clean(headName);
        building.FacilityHeadPhone = Clean(headPhone);
        building.OnSiteForce = force is null ? null : JsonSerializer.Serialize(force with { LeaderName = Clean(force.LeaderName), LeaderPhone = Clean(force.LeaderPhone) }, Json);
        building.EmergencyContacts = JsonSerializer.Serialize((contacts ?? []).Select(c => new EmergencyContact(c.Name.Trim(), c.Phone.Trim(), Clean(c.Role))), Json);
    }

    public static OnSiteForce? Force(Building building) =>
        string.IsNullOrEmpty(building.OnSiteForce) ? null : JsonSerializer.Deserialize<OnSiteForce>(building.OnSiteForce, Json);

    public static IReadOnlyList<EmergencyContact> Contacts(Building building) =>
        string.IsNullOrEmpty(building.EmergencyContacts) ? [] : JsonSerializer.Deserialize<List<EmergencyContact>>(building.EmergencyContacts, Json) ?? [];

    /// <summary>Hash input of the facility fields, so a replayed create with other facility data is a key conflict.</summary>
    public static object Normalized(decimal? floorArea, string? headName, string? headPhone, OnSiteForce? force, IReadOnlyList<EmergencyContact>? contacts) => new
    {
        floorArea, headName = headName?.Trim(), headPhone = headPhone?.Trim(),
        force = force is null ? null : force with { LeaderName = force.LeaderName?.Trim(), LeaderPhone = force.LeaderPhone?.Trim() },
        contacts = contacts?.Select(c => new { name = c?.Name?.Trim(), phone = c?.Phone?.Trim(), role = c?.Role?.Trim() })
    };
}
