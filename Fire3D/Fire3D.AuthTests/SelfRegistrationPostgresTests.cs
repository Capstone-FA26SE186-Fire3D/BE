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
        // A database that already held such legacy rows keeps them: AlignAuthSchemaWithV7 skips the v7 CHECK there.
        await ExecuteAsync("ALTER TABLE users DROP CONSTRAINT check_trainee_username_required;");
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

    [PostgresFact]
    public async Task Organization_registration_accepts_optional_unique_username_and_keeps_proof_on_conflict()
    {
        var traineeProof = await VerifyRegistrationEmailAsync("taken@example.test");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register/trainee", new
        {
            email = "taken@example.test", username = "fire.owner", password = "StrongPassword12!",
            confirmPassword = "StrongPassword12!", registrationToken = traineeProof
        })).StatusCode);

        var proof = await VerifyRegistrationEmailAsync("named@example.test");
        var form = new
        {
            email = "named@example.test", password = "StrongPassword12!", confirmPassword = "StrongPassword12!",
            organizationName = "Named Co", organizationAddress = "2 Fire Street", organizationPhoneNumber = "+84123450001",
            registrationToken = proof, username = "ab"
        };
        var invalid = await client.PostAsJsonAsync("/api/auth/register/organization", form);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("\"username\"", await invalid.Content.ReadAsStringAsync());

        var conflict = await client.PostAsJsonAsync("/api/auth/register/organization", form with { username = "FIRE.Owner" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var body = await conflict.Content.ReadAsStringAsync();
        Assert.Contains("USERNAME_EXISTS", body);
        Assert.Contains("\"username\"", body);
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM organizations WHERE name='Named Co'"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM users WHERE email='named@example.test'"));

        // The proof survives both failures; the corrected form succeeds with it.
        var created = await client.PostAsJsonAsync("/api/auth/register/organization", form with { username = " Owner.Named " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("owner.named", await ScalarAsync("SELECT username FROM users WHERE email='named@example.test'"));

        var blankProof = await VerifyRegistrationEmailAsync("blank@example.test");
        var blank = await client.PostAsJsonAsync("/api/auth/register/organization", form with
        {
            email = "blank@example.test", organizationName = "Blank Co", organizationPhoneNumber = "+84123450002",
            registrationToken = blankProof, username = "  "
        });
        Assert.Equal(HttpStatusCode.Created, blank.StatusCode);
        Assert.Equal(DBNull.Value, await ScalarAsync("SELECT username FROM users WHERE email='blank@example.test'") ?? DBNull.Value);
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
