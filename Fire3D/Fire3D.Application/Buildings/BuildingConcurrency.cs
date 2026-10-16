using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fire3D.Application.Buildings;

public enum BuildingWriteOutcome { Written, Replayed, Rejected, Stale, KeyConflict }

/// <summary>Building ETag: the quoted updated_at in microseconds since the Unix epoch (PostgreSQL precision).
/// The v7 buildings table has no revision column; every Building write moves updated_at forward by at least 1 µs.</summary>
public static class BuildingEtag
{
    public static long Version(DateTime updatedAt) =>
        (DateTime.SpecifyKind(updatedAt, DateTimeKind.Utc).Ticks - DateTime.UnixEpoch.Ticks) / 10;

    public static string Format(DateTime updatedAt) => $"\"{Version(updatedAt).ToString(CultureInfo.InvariantCulture)}\"";

    public static bool TryParse(string? value, out long version)
    {
        version = 0;
        var tag = value?.Trim();
        return tag is { Length: > 2 } && tag[0] == '"' && tag[^1] == '"'
            && long.TryParse(tag.AsSpan(1, tag.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out version)
            && version > 0;
    }

    /// <summary>The next updated_at: now truncated to microseconds, strictly after the stored value.</summary>
    public static DateTime Next(DateTime now, DateTime? current)
    {
        var next = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        if (current is { } stored && Version(next) <= Version(stored))
            next = DateTime.UnixEpoch.AddTicks((Version(stored) + 1) * 10);
        return next;
    }
}

internal static class BuildingMapping
{
    internal static BuildingResponse ToResponse(Fire3D.Domain.Entities.Building building) => new(building.Id, building.Name, building.BuildingType,
        building.TotalFloors, building.IsActive, building.OrganizationId, building.CreatedAt, building.UpdatedAt,
        building.BuildingLocation is { } l ? new BuildingLocationResponse(l.Id, l.Address, l.City, l.District, l.Latitude, l.Longitude, l.Geojson) : null,
        building.BuildingContact is { } c ? new BuildingContactResponse(c.Id, c.ContactName, c.ContactRole, c.Phone, c.Email, c.IsPrimary) : null);
}

public static class BuildingCommandKey
{
    public const string CreateOperation = "Building.Create";

    public static bool IsValid(string? key) =>
        key is { Length: >= 1 and <= 128 } && key.All(c => c is >= '!' and <= '~');

    /// <summary>SHA-256 of the normalized create input, including the resolved organization.</summary>
    public static string Hash(Guid organizationId, CreateBuildingRequest request)
    {
        var normalized = new
        {
            organizationId,
            name = request.Name?.Trim(),
            buildingType = request.BuildingType?.Trim(),
            request.TotalFloors,
            location = request.Location is null ? null : new
            {
                address = request.Location.Address?.Trim(), city = request.Location.City?.Trim(), district = request.Location.District?.Trim(),
                request.Location.Latitude, request.Location.Longitude, request.Location.Geojson
            },
            contact = request.Contact is null ? null : new
            {
                contactName = request.Contact.ContactName?.Trim(), contactRole = request.Contact.ContactRole?.Trim(),
                phone = request.Contact.Phone?.Trim(), email = request.Contact.Email?.Trim(), request.Contact.IsPrimary
            }
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized))));
    }
}
