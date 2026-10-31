using System.Text.Json;
using System.Text.Json.Serialization;
using Fire3D.Application.Billing;
using Xunit;

namespace Fire3D.AuthTests;

/// <summary>If-Match and deadline parsing of the billing API. Both run without a database.</summary>
public sealed class BillingETagTests
{
    private static readonly Guid Id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    [Fact]
    public void Format_is_a_strong_etag_with_the_id_without_dashes_and_the_revision()
    {
        Assert.Equal("\"billing-0f8fad5bd9cb469fa16570867728950e-7\"", BillingETag.Format(Id, 7));
    }

    [Fact]
    public void Require_accepts_the_exact_etag_of_the_current_revision()
    {
        BillingETag.Require(BillingETag.Format(Id, 1), Id, 1);
        BillingETag.Require(BillingETag.Format(Id, 4096), Id, 4096);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Require_without_if_match_is_428(string? header)
    {
        var error = Assert.Throws<BillingException>(() => BillingETag.Require(header, Id, 1));
        Assert.Equal(428, error.Status);
        Assert.Equal("IF_MATCH_REQUIRED", error.Code);
    }

    [Theory]
    [InlineData("billing-0f8fad5bd9cb469fa16570867728950e-1")]       // no quotes
    [InlineData("W/\"billing-0f8fad5bd9cb469fa16570867728950e-1\"")] // weak validator
    [InlineData("*")]
    [InlineData("\"billing-0F8FAD5BD9CB469FA16570867728950E-1\"")]   // upper-case hex
    [InlineData("\"billing-0f8fad5b-d9cb-469f-a165-70867728950e-1\"")] // guid with dashes
    [InlineData("\"billing-0f8fad5bd9cb469fa16570867728950e-0\"")]   // revisions start at 1
    [InlineData("\"billing-0f8fad5bd9cb469fa16570867728950e-01\"")]  // no leading zero
    [InlineData("\"billing-0f8fad5bd9cb469fa16570867728950e--1\"")]
    [InlineData("\"billing-0f8fad5bd9cb469fa16570867728950e-x\"")]
    [InlineData("\"billing-0f8fad5bd9cb469fa165708677289-1\"")]      // short id
    [InlineData("\"other-0f8fad5bd9cb469fa16570867728950e-1\"")]
    public void Require_rejects_a_value_that_is_not_an_etag_of_this_api_with_400(string header)
    {
        var error = Assert.Throws<BillingException>(() => BillingETag.Require(header, Id, 1));
        Assert.Equal(400, error.Status);
        Assert.Equal("INVALID_IF_MATCH", error.Code);
    }

    [Fact]
    public void Require_with_an_older_revision_is_412()
    {
        var error = Assert.Throws<BillingException>(() => BillingETag.Require(BillingETag.Format(Id, 2), Id, 3));
        Assert.Equal(412, error.Status);
        Assert.Equal("REVISION_MISMATCH", error.Code);
    }

    [Fact]
    public void Require_with_the_etag_of_another_resource_is_412()
    {
        var other = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var error = Assert.Throws<BillingException>(() => BillingETag.Require(BillingETag.Format(other, 1), Id, 1));
        Assert.Equal(412, error.Status);
    }

    [Fact]
    public void A_malformed_value_is_reported_before_it_is_compared_with_the_revision()
    {
        // 400 (fix your header) must win over 412 (read the resource again), whatever the current revision is.
        var error = Assert.Throws<BillingException>(() => BillingETag.Require("\"billing-x-9\"", Id, 1));
        Assert.Equal(400, error.Status);
    }
}

public sealed class BillingTimestampConverterTests
{
    private sealed record Probe(
        [property: JsonConverter(typeof(BillingTimestampConverter))] DateTimeOffset At,
        [property: JsonConverter(typeof(BillingTimestampConverter))] DateTimeOffset? Until = null);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static Probe Read(string at, string? until = null) =>
        JsonSerializer.Deserialize<Probe>(until is null ? $"{{\"at\":{at}}}" : $"{{\"at\":{at},\"until\":{until}}}", Web)!;

    [Theory]
    [InlineData("\"2026-10-02T12:00:00Z\"")]
    [InlineData("\"2026-10-02T19:00:00+07:00\"")]
    [InlineData("\"2026-10-02T07:00:00-05:00\"")]
    [InlineData("\"2026-10-02T12:00:00.1234567Z\"")]
    public void A_timestamp_with_Z_or_an_offset_identifies_the_same_instant_in_any_zone(string json)
    {
        var value = Read(json).At;
        var expected = DateTimeOffset.Parse("2026-10-02T12:00:00Z");
        Assert.Equal(expected.AddTicks(json.Contains(".1234567") ? 1234567 : 0), value);
    }

    [Fact]
    public void The_offset_given_by_the_client_is_kept()
    {
        Assert.Equal(TimeSpan.FromHours(7), Read("\"2026-10-02T19:00:00+07:00\"").At.Offset);
    }

    [Theory]
    [InlineData("\"2026-10-02T12:00:00\"")]      // no zone: would depend on the server clock
    [InlineData("\"2026-10-02\"")]               // date only
    [InlineData("\"2026-10-02T12:00:00z\"")]     // lower-case z is not accepted
    [InlineData("\"2026-10-02T12:00:00+0700\"")] // offset needs the colon
    [InlineData("\"2026-10-02T12:00:00+07\"")]
    [InlineData("\"notadateZ\"")]
    [InlineData("\"\"")]
    [InlineData("1759406400")]                   // epoch number
    [InlineData("true")]
    public void A_value_that_does_not_name_an_instant_is_rejected_with_the_format_hint(string json)
    {
        var error = Assert.Throws<JsonException>(() => Read(json));
        Assert.Contains("ISO 8601", error.Message);
        Assert.Contains("Z or an explicit timezone offset", error.Message);
    }

    [Fact]
    public void An_optional_deadline_may_be_null_or_missing_but_not_zoneless()
    {
        Assert.Null(Read("\"2026-10-02T12:00:00Z\"", "null").Until);
        Assert.Null(Read("\"2026-10-02T12:00:00Z\"").Until);
        Assert.Throws<JsonException>(() => Read("\"2026-10-02T12:00:00Z\"", "\"2026-12-31T23:59:59\""));
    }

    [Fact]
    public void Written_timestamps_carry_an_offset_so_they_can_be_sent_back()
    {
        var json = JsonSerializer.Serialize(new Probe(DateTimeOffset.Parse("2026-10-02T19:00:00+07:00")), Web);
        var back = JsonSerializer.Deserialize<Probe>(json, Web)!;
        Assert.Equal(DateTimeOffset.Parse("2026-10-02T12:00:00Z"), back.At);
        Assert.Matches("\"at\":\"[^\"]+(Z|[+-][0-9]{2}:[0-9]{2})\"", json);
    }

    [Fact]
    public void The_quotation_deadline_contracts_use_the_converter()
    {
        // ValidFrom of a discount is part of the public contract: a zoneless value must fail model binding, not be guessed.
        const string body = "{\"code\":\"D\",\"discountKind\":\"Percent\",\"discountValue\":5,\"minimumBuildings\":1,\"validFrom\":\"2026-10-02T12:00:00\"}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DiscountWriteRequest>(body, Web));
        var ok = JsonSerializer.Deserialize<DiscountWriteRequest>(body.Replace("12:00:00\"", "12:00:00Z\""), Web)!;
        Assert.Equal(DateTimeOffset.Parse("2026-10-02T12:00:00Z"), ok.ValidFrom);
    }
}
