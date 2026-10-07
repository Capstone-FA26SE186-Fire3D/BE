using System.Net.Mail;
using System.Text.Json;

namespace Fire3D.Application.Buildings;

/// <summary>Shared create/update validation. Database numeric limits and the public text contract are checked before persistence.</summary>
internal static class BuildingValidation
{
    internal static Dictionary<string, string[]> Validate(string? name, string? type, int floors,
        CreateBuildingLocationRequest? location, CreateBuildingContactRequest? contact)
    {
        var errors = new Dictionary<string, string[]>();
        void Text(string field, string? value, int max, bool required = false)
        {
            if ((required && string.IsNullOrWhiteSpace(value)) || value?.Trim().Length > max || value?.Any(char.IsControl) == true)
                errors[field] = [$"{field} {(required ? "is required and " : "")}must contain at most {max} characters without control characters."];
        }
        Text("name", name, 200, true);
        Text("buildingType", type, 100);
        if (floors < 1) errors["totalFloors"] = ["totalFloors must be at least 1."];
        if (contact is not null)
        {
            Text("contact.contactName", contact.ContactName, 255, true);
            Text("contact.contactRole", contact.ContactRole, 100);
            Text("contact.phone", contact.Phone, 50);
            Text("contact.email", contact.Email, 255);
            if (contact.Email is not null && (!MailAddress.TryCreate(contact.Email.Trim(), out var address) || address.Address != contact.Email.Trim()))
                errors["contact.email"] = ["contact.email must be a valid email address."];
        }
        if (location is not null)
        {
            Text("location.city", location.City, 255);
            Text("location.district", location.District, 255);
            if (location.Latitude is < -90 or > 90 || (location.Latitude is decimal lat && decimal.Round(lat, 8) != lat))
                errors["location.latitude"] = ["latitude must be between -90 and 90 with at most 8 decimal places."];
            if (location.Longitude is < -180 or > 180 || (location.Longitude is decimal lon && decimal.Round(lon, 8) != lon))
                errors["location.longitude"] = ["longitude must be between -180 and 180 with at most 8 decimal places."];
            if (location.Geojson is not null && !IsGeoJson(location.Geojson))
                errors["location.geojson"] = ["geojson must contain a valid GeoJSON geometry or Feature with finite WGS84 coordinates."];
        }
        return errors;
    }

    private static bool IsGeoJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            return Geometry(document.RootElement);
        }
        catch (JsonException) { return false; }
    }
    private static bool Geometry(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return false;
        if (type.GetString() == "Feature") return value.TryGetProperty("geometry", out var geometry) && Geometry(geometry);
        if (type.GetString() is "GeometryCollection" or "FeatureCollection")
        {
            var field = type.GetString() == "GeometryCollection" ? "geometries" : "features";
            return value.TryGetProperty(field, out var items) && items.ValueKind == JsonValueKind.Array && items.EnumerateArray().All(Geometry);
        }
        if (!value.TryGetProperty("coordinates", out var coordinates)) return false;
        return type.GetString() switch
        {
            "Point" => Position(coordinates),
            "MultiPoint" => Positions(coordinates, 1),
            "LineString" => Positions(coordinates, 2),
            "MultiLineString" => ArrayOf(coordinates, x => Positions(x, 2)),
            "Polygon" => Polygon(coordinates),
            "MultiPolygon" => ArrayOf(coordinates, Polygon),
            _ => false
        };
    }
    private static bool Position(JsonElement position) => position.ValueKind == JsonValueKind.Array && position.GetArrayLength() >= 2
        && position.EnumerateArray().All(x => x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out var n) && double.IsFinite(n))
        && position[0].GetDouble() is >= -180 and <= 180 && position[1].GetDouble() is >= -90 and <= 90;
    private static bool Positions(JsonElement positions, int minimum) => positions.ValueKind == JsonValueKind.Array
        && positions.GetArrayLength() >= minimum && positions.EnumerateArray().All(Position);
    private static bool ArrayOf(JsonElement array, Func<JsonElement, bool> validate) => array.ValueKind == JsonValueKind.Array
        && array.GetArrayLength() > 0 && array.EnumerateArray().All(validate);
    private static bool Polygon(JsonElement coordinates) => ArrayOf(coordinates, ring => Positions(ring, 4)
        && ring[0].EnumerateArray().Select(x => x.GetDouble()).SequenceEqual(ring[ring.GetArrayLength() - 1].EnumerateArray().Select(x => x.GetDouble())));
}
