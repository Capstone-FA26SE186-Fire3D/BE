using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Self_registration_persists_both_account_types_and_rejects_case_insensitive_username_reuse()
    {
        var trainee = new
        {
            email = "trainee@example.test", username = "Fire.Drill", password = "StrongPassword12!",
            confirmPassword = "StrongPassword12!", fullName = "Trainee"
        };
        var createdTrainee = await client.PostAsJsonAsync("/api/auth/register/trainee", trainee);
        Assert.Equal(HttpStatusCode.Created, createdTrainee.StatusCode);
        Assert.Equal("fire.drill", await ScalarAsync("SELECT username FROM users WHERE email='trainee@example.test'"));
        Assert.Equal("Trainee", await ScalarAsync("SELECT role::text FROM users WHERE email='trainee@example.test'"));

        var duplicate = await client.PostAsJsonAsync("/api/auth/register/trainee", trainee with { username = "FIRE.DRILL", email = "other@example.test" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var organization = new
        {
            email = "owner@example.test", password = "StrongPassword12!", confirmPassword = "StrongPassword12!",
            fullName = "Owner", organizationName = "Fire3D Co", organizationAddress = "1 Fire Street", organizationPhoneNumber = "+84123456789"
        };
        var createdOrganization = await client.PostAsJsonAsync("/api/auth/register/organization", organization);
        Assert.Equal(HttpStatusCode.Created, createdOrganization.StatusCode);
        Assert.Equal("OrganizationUser", await ScalarAsync("SELECT role::text FROM users WHERE email='owner@example.test'"));
        Assert.Equal("1 Fire Street", await ScalarAsync("SELECT address FROM organizations WHERE name='Fire3D Co'"));
    }
}
