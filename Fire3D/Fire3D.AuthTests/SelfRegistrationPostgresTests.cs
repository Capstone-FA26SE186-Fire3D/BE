using System.Net;
using System.Net.Http.Json;
using Fire3D.Application.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Self_registration_persists_both_account_types_and_rejects_case_insensitive_username_reuse()
    {
        var traineeProof = await VerifyRegistrationEmailAsync("trainee@example.test");
        var trainee = new
        {
            email = "trainee@example.test", username = "Fire.Drill", password = "StrongPassword12!",
            confirmPassword = "StrongPassword12!", fullName = "Trainee", registrationToken = traineeProof
        };
        var createdTrainee = await client.PostAsJsonAsync("/api/auth/register/trainee", trainee);
        Assert.Equal(HttpStatusCode.Created, createdTrainee.StatusCode);
        Assert.Equal("fire.drill", await ScalarAsync("SELECT username FROM users WHERE email='trainee@example.test'"));
        Assert.Equal("Trainee", await ScalarAsync("SELECT role::text FROM users WHERE email='trainee@example.test'"));

        var duplicateProof = await VerifyRegistrationEmailAsync("other@example.test");
        var duplicate = await client.PostAsJsonAsync("/api/auth/register/trainee", trainee with { username = "FIRE.DRILL", email = "other@example.test", registrationToken = duplicateProof });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var organizationProof = await VerifyRegistrationEmailAsync("owner@example.test");
        var organization = new
        {
            email = "owner@example.test", password = "StrongPassword12!", confirmPassword = "StrongPassword12!",
            fullName = "Owner", organizationName = "Fire3D Co", organizationAddress = "1 Fire Street", organizationPhoneNumber = "+84123456789", registrationToken = organizationProof
        };
        var createdOrganization = await client.PostAsJsonAsync("/api/auth/register/organization", organization);
        Assert.Equal(HttpStatusCode.Created, createdOrganization.StatusCode);
        Assert.Equal("OrganizationUser", await ScalarAsync("SELECT role::text FROM users WHERE email='owner@example.test'"));
        Assert.Equal("1 Fire Street", await ScalarAsync("SELECT address FROM organizations WHERE name='Fire3D Co'"));
    }

    [PostgresFact]
    public async Task Legacy_trainee_without_a_username_can_still_sign_in()
    {
        var proof = await VerifyRegistrationEmailAsync("legacy@example.test");
        var registration = new
        {
            email = "legacy@example.test", username = "legacy", password = "StrongPassword12!",
            confirmPassword = "StrongPassword12!", registrationToken = proof
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

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("legacy@example.test", "StrongPassword12!"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [PostgresFact]
    public async Task Registration_proof_is_single_use_and_no_user_exists_before_otp_is_verified()
    {
        const string email = "proof@example.test";
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM users WHERE email='proof@example.test'"));
        var proof = await VerifyRegistrationEmailAsync(email);
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM users WHERE email='proof@example.test'"));

        var request = new { email, username = "proof-user", password = "StrongPassword12!", confirmPassword = "StrongPassword12!", registrationToken = proof };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register/trainee", request)).StatusCode);
        var replay = await client.PostAsJsonAsync("/api/auth/register/trainee", request with { username = "proof-replay" });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        var body = await replay.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("EMAIL_VERIFICATION_REQUIRED", body.GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task Resent_registration_otp_invalidates_an_unconsumed_registration_proof()
    {
        const string email = "resend-proof@example.test";
        var firstProof = await VerifyRegistrationEmailAsync(email);
        await ExecuteAsync("UPDATE registration_otp_email_jobs SET created_at=now()-interval '61 seconds' WHERE email='resend-proof@example.test'");
        var secondProof = await VerifyRegistrationEmailAsync(email);
        var baseRequest = new { email, username = "resend-proof", password = "StrongPassword12!", confirmPassword = "StrongPassword12!" };

        var stale = await client.PostAsJsonAsync("/api/auth/register/trainee", new { baseRequest.email, baseRequest.username, baseRequest.password, baseRequest.confirmPassword, registrationToken = firstProof });
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register/trainee",
            new { baseRequest.email, baseRequest.username, baseRequest.password, baseRequest.confirmPassword, registrationToken = secondProof })).StatusCode);
    }

    private async Task<string> VerifyRegistrationEmailAsync(string email)
    {
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/auth/registration/request-otp", new { email })).StatusCode);
        using var scope = factory!.Services.CreateScope();
        var job = Assert.IsType<RegistrationOtpEmailJob>(await scope.ServiceProvider
            .GetRequiredService<IRegistrationOtpDeliveryQueue>().ClaimAsync(default));
        Assert.Equal(email, job.Email);
        var response = await client.PostAsJsonAsync("/api/auth/registration/verify-otp", new { email, otp = job.Otp });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RegistrationOtpVerificationResponse>())!.RegistrationToken;
    }
}
