using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fire3D.Application.Billing;

/// <summary>Billing deadlines must identify an instant, independent of the server's timezone.</summary>
public sealed class BillingTimestampConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader,Type typeToConvert,JsonSerializerOptions options)
    {
        var text=reader.TokenType==JsonTokenType.String ? reader.GetString():null;
        if(text is null || !System.Text.RegularExpressions.Regex.IsMatch(text,@"(Z|[+-][0-9]{2}:[0-9]{2})$")
            || !reader.TryGetDateTimeOffset(out var value))
            throw new JsonException("Use an ISO 8601 timestamp with Z or an explicit timezone offset, e.g. 2026-10-02T12:00:00Z.");
        return value;
    }
    public override void Write(Utf8JsonWriter writer,DateTimeOffset value,JsonSerializerOptions options)=>writer.WriteStringValue(value);
}
