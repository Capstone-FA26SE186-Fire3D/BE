using System.Text.Json.Nodes;
using Fire3D.API.Authorization;
using Fire3D.Application.Administration;
using Fire3D.Application.Ai;
using Fire3D.Application.Authentication;
using Fire3D.Application.Buildings;
using Fire3D.Application.Editor;
using Fire3D.Application.Ifc;
using Fire3D.Application.Storage;
using Fire3D.Domain.Entities;
using Microsoft.AspNetCore.Http;
using SkiaSharp;
using Xunit;

namespace Fire3D.AuthTests;

// Unit tests of small helpers that controllers and gates call directly (no database, no HTTP).
public sealed class BuildingRulesUnitTests
{
    [Theory]
    [InlineData("Apartment", 4, 999, "BelowThreshold")]
    [InlineData("Apartment", 6, 999, "Group2")]
    [InlineData("Apartment", 5, null, "Group2")]
    [InlineData("Apartment", 1, 1000, "Group2")]
    [InlineData("Apartment", 7, null, "Group1")]
    [InlineData("Apartment", 1, 3000, "Group1")]
    [InlineData("School", 2, 1499, "BelowThreshold")]
    [InlineData("School", 3, null, "Group2")]
    [InlineData("School", 1, 1500, "Group2")]
    [InlineData("School", 5, null, "Group1")]
    [InlineData("School", 1, 3000, "Group1")]
    [InlineData(null, 30, 50000, null)]
    [InlineData("Hotel", 30, 50000, null)]
    public void Risk_group_follows_Phu_luc_II_of_Nghi_dinh_105(string? type, int floors, int? area, string? expected) =>
        Assert.Equal(expected, BuildingRiskGroup.Compute(type, floors, area));

    [Fact]
    public void Only_apartment_and_school_are_building_types()
    {
        Assert.Equal(new[] { "Apartment", "School" }, BuildingTypes.All);
    }

    private static Dictionary<string, string[]> Facility(decimal? area = null, string? headName = null, string? headPhone = null, OnSiteForce? force = null, IReadOnlyList<EmergencyContact>? contacts = null)
    {
        var errors = new Dictionary<string, string[]>();
        BuildingFacility.Validate(errors, area, headName, headPhone, force, contacts);
        return errors;
    }

    [Fact]
    public void Facility_fields_are_bounded_and_phones_are_short_numbers_or_full_ones()
    {
        Assert.Empty(Facility(1250.25m, "Nguyễn Văn A", "114", new(12, "Trần B", "+84 (28) 3822-1234", true, false), [new("Công an PCCC quận 1", "114", "Cảnh sát PCCC")]));
        Assert.Contains("floorAreaSquareMeters", Facility(area: 0).Keys);
        Assert.Contains("floorAreaSquareMeters", Facility(area: 10_000_001).Keys);
        Assert.Contains("floorAreaSquareMeters", Facility(area: 12.345m).Keys);
        Assert.Contains("facilityHeadPhone", Facility(headPhone: "12").Keys);
        Assert.Contains("facilityHeadPhone", Facility(headPhone: "call me").Keys);
        Assert.Contains("facilityHeadName", Facility(headName: new string('a', 201)).Keys);
        Assert.Contains("facilityHeadName", Facility(headName: "tab\there").Keys);
        Assert.Contains("onSiteForce.teamSize", Facility(force: new(-1, null, null, false, false)).Keys);
        Assert.Contains("onSiteForce.leaderPhone", Facility(force: new(1, null, "x", false, false)).Keys);
    }

    [Fact]
    public void Emergency_contacts_need_name_and_phone_and_at_most_twenty()
    {
        var many = Enumerable.Range(0, 21).Select(i => new EmergencyContact($"C{i}", "114")).ToList();
        Assert.Contains("emergencyContacts", Facility(contacts: many).Keys);
        var errors = Facility(contacts: [null!, new("", "114"), new("Bảo vệ", "")]);
        Assert.Contains("emergencyContacts[0]", errors.Keys);
        Assert.Contains("emergencyContacts[1].name", errors.Keys);
        Assert.Contains("emergencyContacts[2].phone", errors.Keys);
    }

    [Fact]
    public void Facility_values_are_trimmed_when_stored_and_read_back()
    {
        var building = new Building();
        BuildingFacility.Apply(building, 900m, "  Lê C ", " ", new(3, " Phạm D ", " 0909 123 456 ", true, true), [new(" Bảo vệ ", " 0281 ", " ")]);
        Assert.Equal(("Lê C", (string?)null), (building.FacilityHeadName, building.FacilityHeadPhone));
        Assert.Equal(new OnSiteForce(3, "Phạm D", "0909 123 456", true, true), BuildingFacility.Force(building));
        Assert.Equal(new EmergencyContact("Bảo vệ", "0281", null), Assert.Single(BuildingFacility.Contacts(building)));
        var empty = new Building { OnSiteForce = null, EmergencyContacts = "" };
        Assert.Null(BuildingFacility.Force(empty));
        Assert.Empty(BuildingFacility.Contacts(empty));
    }

    [Fact]
    public void Building_etag_is_microseconds_and_always_moves_forward()
    {
        var at = new DateTime(2026, 11, 26, 1, 2, 3, DateTimeKind.Utc).AddTicks(1234567);
        var tag = BuildingEtag.Format(at);
        Assert.True(BuildingEtag.TryParse(tag, out var version));
        Assert.Equal(BuildingEtag.Version(at), version);
        Assert.Equal(0, BuildingEtag.Next(at, null).Ticks % 10);
        // A clock that did not move (or moved back) still gives a strictly newer value.
        Assert.True(BuildingEtag.Version(BuildingEtag.Next(at, at)) > BuildingEtag.Version(at));
        Assert.True(BuildingEtag.Version(BuildingEtag.Next(at.AddSeconds(-5), at)) > BuildingEtag.Version(at));
        foreach (var bad in new[] { "123", "\"0\"", "\"-5\"", "W/\"5\"", "\"\"", "\"1 2\"", null })
            Assert.False(BuildingEtag.TryParse(bad, out _), bad);
    }

    [Fact]
    public void Building_idempotency_keys_are_1_to_128_visible_ascii_characters()
    {
        Assert.True(BuildingCommandKey.IsValid("create-building-1"));
        Assert.True(BuildingCommandKey.IsValid(new string('k', 128)));
        Assert.False(BuildingCommandKey.IsValid(new string('k', 129)));
        Assert.False(BuildingCommandKey.IsValid(""));
        Assert.False(BuildingCommandKey.IsValid("has space"));
        Assert.False(BuildingCommandKey.IsValid("tòa-nhà"));
        Assert.False(BuildingCommandKey.IsValid(null));
    }

    [Fact]
    public void Signed_plan_upload_is_one_pdf_up_to_20_mb()
    {
        Assert.Equal("body", Assert.Single(BuildingPlanDocumentRules.Validate(null)).Key);
        Assert.Empty(BuildingPlanDocumentRules.Validate(new("PC06 đã ký.pdf", "Application/PDF", 1)));
        var errors = BuildingPlanDocumentRules.Validate(new("bad\u0001.pdf", "image/png", BuildingPlanDocumentRules.MaxBytes + 1));
        Assert.Equal(new[] { "contentType", "fileName", "sizeBytes" }, errors.Keys.Order().ToArray());
        Assert.Contains("sizeBytes", BuildingPlanDocumentRules.Validate(new("a.pdf", "application/pdf", 0)).Keys);
        Assert.Contains("fileName", BuildingPlanDocumentRules.Validate(new(" ", "application/pdf", 10)).Keys);
        Assert.Equal(BuildingPlanDocumentRules.InputHash(new(" a.pdf ", "application/pdf", 10)), BuildingPlanDocumentRules.InputHash(new("a.pdf", "x", 10)));
        Assert.NotEqual(BuildingPlanDocumentRules.InputHash(new("a.pdf", "application/pdf", 10)), BuildingPlanDocumentRules.InputHash(new("a.pdf", "application/pdf", 11)));
        var building = Guid.NewGuid(); var id = Guid.NewGuid();
        Assert.Equal($"buildings/{building:N}/signed-plans/{id:N}.pdf", BuildingPlanDocumentRules.FileKey(building, id));
        Assert.StartsWith($"buildings/{building:N}/signed-plans/staging/", BuildingPlanDocumentRules.StagingKey(building, id));
    }
}

public sealed class AccountRulesUnitTests
{
    [Fact]
    public void Profile_etag_round_trips_positive_revisions_only()
    {
        Assert.Equal("\"42\"", ProfileEtag.Format(42));
        Assert.True(ProfileEtag.TryParse(" \"42\" ", out var revision));
        Assert.Equal(42, revision);
        foreach (var bad in new[] { "42", "\"0\"", "\"+1\"", "\"4 2\"", "W/\"42\"", "\"", null })
            Assert.False(ProfileEtag.TryParse(bad, out _), bad);
    }

    [Fact]
    public void Reset_emails_are_normalized_and_passwords_are_6_to_128_characters()
    {
        Assert.Equal("user@example.test", PasswordResetValidation.NormalizeEmail("  User@Example.TEST "));
        Assert.Null(PasswordResetValidation.NormalizeEmail("Name <user@example.test>"));
        Assert.Null(PasswordResetValidation.NormalizeEmail("no-at-sign"));
        Assert.Null(PasswordResetValidation.NormalizeEmail(new string('a', 250) + "@x.vn"));
        Assert.Null(PasswordResetValidation.NormalizeEmail(null));
        Assert.True(PasswordResetValidation.ValidPassword("123456"));
        Assert.True(PasswordResetValidation.ValidPassword(new string('p', 128)));
        Assert.False(PasswordResetValidation.ValidPassword("12345"));
        Assert.False(PasswordResetValidation.ValidPassword(new string('p', 129)));
        Assert.False(PasswordResetValidation.ValidPassword("      "));
    }

    [Fact]
    public void Admin_lists_sort_by_an_allowed_field_newest_first_by_default()
    {
        Assert.True(AdministrationSort.TryParse(null, AdministrationSort.OrganizationFields, out var field, out var descending));
        Assert.Equal(("createdAt", true), (field, descending));
        Assert.True(AdministrationSort.TryParse(" FULLNAME ", AdministrationSort.AccountFields, out field, out descending));
        Assert.Equal(("fullName", false), (field, descending));
        Assert.True(AdministrationSort.TryParse("-lastLoginAt", AdministrationSort.AccountFields, out field, out descending));
        Assert.Equal(("lastLoginAt", true), (field, descending));
        Assert.False(AdministrationSort.TryParse("email", AdministrationSort.OrganizationFields, out field, out _));
        Assert.Equal("", field);
        Assert.False(AdministrationSort.TryParse("password", AdministrationSort.AccountFields, out _, out _));
    }

    [Theory]
    [InlineData("/api/expert/reviews", true)]
    [InlineData("/api/Expert/reviews/1/approve", true)]
    [InlineData("/api/auth/me", true)]
    [InlineData("/api/me/avatar", true)]
    [InlineData("/health/ready", true)]
    [InlineData("/api/buildings", false)]
    [InlineData("/api/expertise", false)]
    [InlineData("/api/learn/posts", false)]
    [InlineData("/api", false)]
    public void Experts_reach_only_the_review_queue_and_their_own_account(string path, bool allowed) =>
        Assert.Equal(allowed, ExpertRouteGuard.Allows(new PathString(path)));
}

public sealed class ContentRulesUnitTests
{
    [Fact]
    public void Ai_index_states_map_to_four_statuses()
    {
        var id = Guid.NewGuid();
        Assert.Equal(new AiIndexInfo("Ready", null), AiIndexStatus.From(new(id, "ready", null)));
        Assert.Equal(new AiIndexInfo("Preparing", null), AiIndexStatus.From(new(id, "queued", "IGNORED")));
        Assert.Equal(new AiIndexInfo("Failed", "AI_SOURCE_HASH_MISMATCH"), AiIndexStatus.From(new(id, "failed", "AI_SOURCE_HASH_MISMATCH")));
        Assert.Equal(new AiIndexInfo("Unknown", null), AiIndexStatus.From(new(id, "READY", null)));
    }

    [Fact]
    public void Equipment_decisions_are_1_to_500_distinct_with_a_catalog_key_when_retyped()
    {
        Assert.Equal("$.items", Assert.Single(EquipmentDecisionRules.Validate(null)).Path);
        Assert.Equal("$.items", Assert.Single(EquipmentDecisionRules.Validate(new([]))).Path);
        Assert.Empty(EquipmentDecisionRules.Validate(new([new("c1", "Confirmed"), new("c2", "Retyped", "extinguisher.portable"), new("c3", "Rejected", null, "Không phải bình")])));
        var issues = EquipmentDecisionRules.Validate(new([
            new("c1", "Confirmed"), new("c1", "Maybe"), new("c2", "Retyped"), new("c3", "Rejected", "door.exit"), new("c4", "Confirmed", "Bad Key"), new("", "Confirmed"),
            new("c5", "Confirmed", null, "<script>")]));
        Assert.Contains(issues, x => x.Path == "$.items[1].candidateId" && x.Code == "ID_DUPLICATE");
        Assert.Contains(issues, x => x.Path == "$.items[1].decision" && x.Code == "VALUE_UNSUPPORTED");
        Assert.Contains(issues, x => x.Path == "$.items[2].catalogKey" && x.Code == "FIELD_REQUIRED");
        Assert.Contains(issues, x => x.Path == "$.items[3].catalogKey" && x.Code == "VALUE_UNSUPPORTED");
        Assert.Contains(issues, x => x.Path == "$.items[4].catalogKey" && x.Code == "VALUE_UNSUPPORTED");
        Assert.Contains(issues, x => x.Path == "$.items[5].candidateId" && x.Code == "FIELD_REQUIRED");
        Assert.Contains(issues, x => x.Path == "$.items[6].note" && x.Code == "MARKUP_NOT_ALLOWED");
        Assert.Equal("$.items", Assert.Single(EquipmentDecisionRules.Validate(new(Enumerable.Range(0, 501).Select(i => new EquipmentDecisionInput($"c{i}", "Confirmed")).ToList()))).Path);
    }

    [Fact]
    public void Rubric_schema_version_is_the_string_1_and_the_catalog_explains_every_metric()
    {
        Assert.True(RubricContract.IsSupportedVersion(JsonValue.Create("1")));
        Assert.False(RubricContract.IsSupportedVersion(JsonValue.Create(1)));
        Assert.False(RubricContract.IsSupportedVersion(JsonValue.Create("v1")));
        Assert.False(RubricContract.IsSupportedVersion(null));
        Assert.Contains("\"1\"", RubricContract.UnsupportedMessage);
        var metrics = RubricContract.Catalog.Metrics.ToDictionary(x => x.Id, x => x.Description);
        Assert.Contains("RouteBaseline", metrics["route_efficiency"]);
        Assert.Contains("Not evaluable", metrics["step_count"]);
        Assert.Equal("Number of accepted DoorOpened events (rejected events are not counted).",
            RubricContract.CatalogFor(new Dictionary<string, string?> { ["doors_opened"] = "DoorOpened" }).Metrics.Single().Description);
        Assert.Equal(new[] { "gte", "lte", "eq" }, RubricContract.Catalog.Operators);
        Assert.Equal(100, RubricContract.Catalog.MaxCriteria);
    }
}

public sealed class ImageInspectionUnitTests
{
    private static byte[] Image(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.OrangeRed);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    [Fact]
    public void A_complete_image_of_the_declared_type_reports_its_size()
    {
        Assert.Equal((32, 16), ImageInspection.Inspect(Image(32, 16, SKEncodedImageFormat.Png), "image/png", 1_000_000));
        Assert.Equal((20, 10), ImageInspection.Inspect(Image(20, 10, SKEncodedImageFormat.Jpeg), "IMAGE/JPEG", 1_000_000));
    }

    [Fact]
    public void Wrong_type_too_large_truncated_or_not_an_image_is_refused()
    {
        var png = Image(32, 16, SKEncodedImageFormat.Png);
        Assert.Null(ImageInspection.Inspect(png, "image/jpeg", 1_000_000));
        Assert.Null(ImageInspection.Inspect(png, "image/png", png.Length - 1));
        Assert.Null(ImageInspection.Inspect(png[..(png.Length / 2)], "image/png", 1_000_000));
        Assert.Null(ImageInspection.Inspect("%PDF-1.7 not an image"u8.ToArray(), "image/png", 1_000_000));
        Assert.Null(ImageInspection.Inspect([], "image/png", 1_000_000));
        Assert.Null(ImageInspection.Inspect(null, "image/png", 1_000_000));
        Assert.Null(ImageInspection.Inspect(Image(ImageInspection.MaxSide + 1, 1, SKEncodedImageFormat.Png), "image/png", 10_000_000));
    }
}
