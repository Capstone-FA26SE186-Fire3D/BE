using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Authentication;
using Fire3D.Application.Buildings;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fire3D.AuthTests;

// BE#212: risk group of Phụ lục II Nghị định 105/2025/NĐ-CP at its thresholds, and the facility field rules.
public sealed class BuildingPc06RuleTests
{
    [Theory]
    [InlineData("Apartment", 6, 900.0, "Group2")]        // issue case
    [InlineData("Apartment", 4, 3000.0, "Group1")]       // issue case
    [InlineData("School", 2, 1400.0, "BelowThreshold")]  // issue case
    [InlineData("Apartment", 7, null, "Group1")]
    [InlineData("Apartment", 5, null, "Group2")]
    [InlineData("Apartment", 4, 2999.99, "Group2")]
    [InlineData("Apartment", 4, 1000.0, "Group2")]
    [InlineData("Apartment", 4, 999.99, "BelowThreshold")]
    [InlineData("School", 5, null, "Group1")]
    [InlineData("School", 4, 3000.0, "Group1")]
    [InlineData("School", 3, null, "Group2")]
    [InlineData("School", 1, 1500.0, "Group2")]
    [InlineData("School", 2, 1499.99, "BelowThreshold")]
    public void Risk_group_follows_the_decree_thresholds(string type, int floors, double? area, string expected) =>
        Assert.Equal(expected, BuildingRiskGroup.Compute(type, floors, area is null ? null : (decimal)area.Value));

    [Fact]
    public void Risk_group_needs_a_building_type() => Assert.Null(BuildingRiskGroup.Compute(null, 10, 10000m));

    [Fact]
    public void Facility_fields_are_validated()
    {
        var errors = new Dictionary<string, string[]>();
        BuildingFacility.Validate(errors, 0m, new string('x', 201), "12", new OnSiteForce(-1, null, "abc", true, false),
            Enumerable.Range(0, 21).Select(i => new EmergencyContact("Công an PCCC", "1")).ToList());
        Assert.Equal(["emergencyContacts", "facilityHeadName", "facilityHeadPhone", "floorAreaSquareMeters", "onSiteForce.leaderPhone", "onSiteForce.teamSize"],
            errors.Keys.Where(k => !k.StartsWith("emergencyContacts[")).Order().ToArray());
        Assert.Contains("emergencyContacts[0].phone", errors.Keys);
        var ok = new Dictionary<string, string[]>();
        BuildingFacility.Validate(ok, 1234.5m, "Nguyễn Văn A", "0912 345 678", new OnSiteForce(12, "Trần B", "+84 912345678", true, true),
            [new EmergencyContact("Cảnh sát PCCC phường", "0243 123 4567", "Công an")]);
        Assert.Empty(ok);
    }
}

public sealed partial class AuthIntegrationTests
{
    // BE#212 acceptance through the API: only Apartment/School, server-computed risk group, facility fields round trip.
    [PostgresFact]
    public async Task Buildings_take_only_apartment_or_school_and_report_the_risk_group()
    {
        var tenant = await SeedAiTenant();
        using var org = await RoleClient("OrganizationUser", tenant.Org);
        var hospital = await PostBuilding(org, new { name = "Bệnh viện", buildingType = "Hospital", totalFloors = 3 });
        Assert.Equal(HttpStatusCode.BadRequest, hospital.StatusCode);
        Assert.True((await hospital.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("buildingType", out _));

        var created = await PostBuilding(org, new
        {
            name = "Chung cư A", buildingType = "Apartment", totalFloors = 6, floorAreaSquareMeters = 900m,
            facilityHeadName = "Nguyễn Văn A", facilityHeadPhone = "0912 345 678",
            onSiteForce = new { teamSize = 10, leaderName = "Trần B", leaderPhone = "0987654321", availableInHours = true, availableOffHours = false },
            emergencyContacts = new[] { new { name = "Cảnh sát PCCC", phone = "114", role = (string?)null } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var building = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = building.GetProperty("id").GetGuid();
        Assert.Equal(("Group2", 900m, "Nguyễn Văn A"), (building.GetProperty("riskGroup").GetString(), building.GetProperty("floorAreaSquareMeters").GetDecimal(), building.GetProperty("facilityHeadName").GetString()));
        Assert.Equal(10, building.GetProperty("onSiteForce").GetProperty("teamSize").GetInt32());
        Assert.False(building.GetProperty("onSiteForce").GetProperty("availableOffHours").GetBoolean());
        Assert.Equal("114", building.GetProperty("emergencyContacts")[0].GetProperty("phone").GetString());

        var updated = await SendBuilding(org, HttpMethod.Put, id, new { name = "Chung cư A", buildingType = "Apartment", totalFloors = 4, floorAreaSquareMeters = 3000m }, created.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var after = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Group1", 0), (after.GetProperty("riskGroup").GetString(), after.GetProperty("emergencyContacts").GetArrayLength()));
        Assert.Equal(JsonValueKind.Null, after.GetProperty("onSiteForce").ValueKind);
        // A legacy Building without a valid type must choose one on the next edit.
        var noType = await SendBuilding(org, HttpMethod.Put, id, new { name = "Chung cư A", buildingType = (string?)null, totalFloors = 4 }, updated.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.BadRequest, noType.StatusCode);

        var school = await (await PostBuilding(org, new { name = "Trường B", buildingType = "School", totalFloors = 2, floorAreaSquareMeters = 1400m })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BelowThreshold", school.GetProperty("riskGroup").GetString());
        var list = await org.GetFromJsonAsync<JsonElement>("/api/buildings?pageSize=100");
        Assert.Contains(list.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == id && x.GetProperty("riskGroup").GetString() == "Group1");

        // The database keeps only the two types.
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync($"UPDATE buildings SET building_type='Office' WHERE id='{id}'"));
        Assert.Equal(HttpStatusCode.NotFound, (await org.GetAsync($"/api/buildings/{id}/signed-plan")).StatusCode);
    }

    // BE#212: the signed PC06 plan is a private PDF; anything else is refused before or after the upload.
    [PostgresFact]
    public async Task Signed_plan_accepts_only_a_pdf_and_stays_private()
    {
        var tenant = await SeedAiTenant();
        var other = await SeedAiTenant();
        var pdf = "%PDF-1.7\n% Phương án PC06 đã ký\n%%EOF"u8.ToArray();
        var notPdf = "PK\u0003\u0004 not a pdf"u8.ToArray();
        using var scope = factory!.Services.CreateScope();
        BuildingPlanDocumentService Service(byte[] bytes) => new(scope.ServiceProvider.GetRequiredService<IBuildingPlanDocumentStore>(),
            new KnowledgeSourceFileServiceTests.Storage(bytes, "application/pdf"), scope.ServiceProvider.GetRequiredService<IAuthStore>(), TimeProvider.System);

        var word = await Service(pdf).CreateUploadIntentAsync(tenant.User, tenant.Building, "word", new("phuong-an.docx", "application/msword", 100), default);
        Assert.Equal(("VALIDATION_ERROR", 400), (word.Error?.Code, word.Error?.Status));
        Assert.True(word.Error!.Errors!.ContainsKey("contentType"));

        var fake = Service(notPdf);
        var fakeIntent = await fake.CreateUploadIntentAsync(tenant.User, tenant.Building, "fake", new("phuong-an.pdf", "application/pdf", notPdf.Length), default);
        Assert.True(fakeIntent.IsSuccess, fakeIntent.Error?.Code);
        var refused = await fake.CompleteUploadAsync(tenant.User, tenant.Building, fakeIntent.Value!.UploadId, default);
        Assert.Equal(("SIGNED_PLAN_NOT_PDF", 422), (refused.Error?.Code, refused.Error?.Status));
        Assert.Equal(DBNull.Value, await ScalarAsync($"SELECT signed_plan_document_id FROM buildings WHERE id='{tenant.Building}'"));

        var real = Service(pdf);
        var intent = await real.CreateUploadIntentAsync(tenant.User, tenant.Building, "plan", new("phuong-an.pdf", "application/pdf", pdf.Length), default);
        var stored = await real.CompleteUploadAsync(tenant.User, tenant.Building, intent.Value!.UploadId, default);
        Assert.True(stored.IsSuccess, stored.Error?.Code);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pdf)), stored.Value!.Sha256);
        Assert.Equal(stored.Value.Id, (Guid)(await ScalarAsync($"SELECT signed_plan_document_id FROM buildings WHERE id='{tenant.Building}'"))!);
        Assert.Equal(stored.Value.Id, (await real.CompleteUploadAsync(tenant.User, tenant.Building, intent.Value.UploadId, default)).Value!.Id);
        Assert.StartsWith("https://s3.test/get/buildings/", (await real.SignedUrlAsync(tenant.User, tenant.Building, default)).Value);

        // Another organization's user reaches neither the upload nor the file.
        Assert.Equal(403, (await real.SignedUrlAsync(other.User, tenant.Building, default)).Error?.Status);
        Assert.Equal(403, (await real.CompleteUploadAsync(other.User, tenant.Building, intent.Value.UploadId, default)).Error?.Status);
        // The stored document is immutable.
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync($"UPDATE building_plan_documents SET file_name='x.pdf' WHERE id='{stored.Value.Id}'"));
    }
}
