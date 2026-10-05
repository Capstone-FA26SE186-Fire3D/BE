using System.Text.Json;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class GoogleAuthContractTests
{
    [BillingPostgresFact]
    public async Task Openapi_documents_anonymous_onboarding_and_protected_explicit_link()
    {
        await using var db = await BillingDatabase.Create();
        using var factory = new BillingApiTests.Factory(db, new FakePayos());
        using var client = factory.CreateClient();
        using var json = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = json.RootElement.GetProperty("paths");
        var onboarding = paths.GetProperty("/api/auth/google/onboarding/complete").GetProperty("post");
        Assert.True(!onboarding.TryGetProperty("security", out var security) || security.GetArrayLength() == 0);
        Assert.True(onboarding.GetProperty("responses").TryGetProperty("201", out _));
        Assert.False(onboarding.GetProperty("responses").TryGetProperty("200", out _));
        Assert.True(onboarding.GetProperty("responses").TryGetProperty("503", out _));
        var link = paths.GetProperty("/api/me/link-google").GetProperty("post");
        Assert.NotEmpty(link.GetProperty("security").EnumerateArray());
        Assert.True(link.GetProperty("responses").TryGetProperty("503", out _));
        var schemas = json.RootElement.GetProperty("components").GetProperty("schemas");
        var proof = schemas.GetProperty("GoogleExchangeResponse").GetProperty("properties");
        Assert.True(proof.TryGetProperty("onboardingToken", out _)); Assert.True(proof.TryGetProperty("expiresAt", out _));
        Assert.True(proof.GetProperty("onboardingToken").GetProperty("deprecated").GetBoolean());
        Assert.True(proof.GetProperty("expiresAt").GetProperty("deprecated").GetBoolean());
        Assert.True(proof.TryGetProperty("onboarding", out _));
        var details = schemas.GetProperty("GoogleOnboardingDetails").GetProperty("properties");
        foreach (var field in new[] { "token", "expiresAt", "email", "displayName" }) Assert.True(details.TryGetProperty(field, out _));
        var complete = schemas.GetProperty("GoogleOnboardingCompleteRequest");
        Assert.False(complete.GetProperty("additionalProperties").GetBoolean());
        var types = complete.GetProperty("properties").GetProperty("accountType").GetProperty("enum").EnumerateArray()
            .Select(x => x.GetString()).ToArray();
        Assert.Equal(new[] { "trainee", "organization", "Trainee", "OrganizationUser" }, types);
        Assert.DoesNotContain("PlatformAdmin", types);
        var request = schemas.GetProperty("GoogleLinkRequest").GetProperty("properties");
        Assert.True(request.TryGetProperty("currentPassword", out _)); Assert.True(request.TryGetProperty("idToken", out _));
        Assert.False(request.TryGetProperty("userId", out _)); Assert.False(request.TryGetProperty("role", out _));
    }
}
