using System.Net;
using System.Net.Http.Json;
using Fire3D.Application.Authentication;
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

    [PostgresFact]
    public async Task Legacy_trainee_without_a_username_can_still_sign_in()
    {
        var registration = new
        {
            email = "legacy@example.test", username = "legacy", password = "StrongPassword12!",
            confirmPassword = "StrongPassword12!"
        };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register/trainee", registration)).StatusCode);
        await ExecuteAsync("ALTER TABLE users DISABLE TRIGGER users_require_username_for_new_trainee;");
        try
        {
            await ExecuteAsync("UPDATE users SET username=NULL WHERE email='legacy@example.test';");
        }
        finally
        {
            await ExecuteAsync("ALTER TABLE users ENABLE TRIGGER users_require_username_for_new_trainee;");
        }

        await ExecuteAsync("UPDATE users SET email_verified_at=now(), registration_expires_at=NULL WHERE email='legacy@example.test';");
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("legacy@example.test", "StrongPassword12!"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }
}
