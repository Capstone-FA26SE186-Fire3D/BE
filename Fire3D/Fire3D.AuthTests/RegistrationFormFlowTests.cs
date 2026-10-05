using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Fire3D.Application.Authentication;
using Microsoft.Extensions.DependencyInjection;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    [PostgresFact]
    public async Task Parallel_requests_for_different_emails_cannot_overrun_the_same_ip_quota()
    {
        const string ip = "192.0.2.77";
        async Task<AuthResult<bool>> Request(string email)
        {
            using var scope = factory!.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IRegistrationOtpService>().RequestOtpAsync(email, ip, default);
        }
        for (var i = 0; i < 19; i++) Assert.True((await Request($"seed-{i}@example.test")).IsSuccess);
        // Force the count/insert window to overlap using real concurrent database connections.
        await ExecuteAsync("""
            CREATE FUNCTION otp_test_slow_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_sleep(0.1); RETURN NEW; END $$;
            CREATE TRIGGER otp_test_slow_insert BEFORE INSERT ON registration_otp_email_jobs FOR EACH ROW EXECUTE FUNCTION otp_test_slow_insert();
            """);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Request($"parallel-{i}@example.test")));
        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(5, results.Count(result => result.Error?.Code == "OTP_RATE_LIMITED"));
        Assert.Equal(20L, await ScalarAsync("SELECT count(*) FROM registration_otp_email_jobs WHERE remote_address='192.0.2.77'"));
    }

    [PostgresFact]
    public async Task Normalized_email_migration_refuses_legacy_duplicates_without_changing_accounts()
    {
        // Seed a fresh historical database; never downgrade security hardening to reproduce legacy data.
        var legacyName=databaseName+"_legacy";
        await using var admin=new NpgsqlConnection(adminConnection);await admin.OpenAsync();
        await new NpgsqlCommand($"CREATE DATABASE \"{legacyName}\"",admin).ExecuteNonQueryAsync();
        try
        {
            var legacyConnection=new NpgsqlConnectionStringBuilder(testConnection){Database=legacyName}.ConnectionString;
            await using var db=new Fire3DDbContext(new DbContextOptionsBuilder<Fire3DDbContext>().UseNpgsql(legacyConnection).Options);
            await db.GetService<IMigrator>().MigrateAsync("20261002133000_AddPayosProvisioningGates");
            await using var connection=new NpgsqlConnection(legacyConnection);await connection.OpenAsync();
            await new NpgsqlCommand("INSERT INTO users(id,email,role) VALUES(gen_random_uuid(),'admin@example.test','PlatformAdmin'),(gen_random_uuid(),'ADMIN@EXAMPLE.TEST','PlatformAdmin')",connection).ExecuteNonQueryAsync();
            var error=await Assert.ThrowsAsync<PostgresException>(()=>db.Database.MigrateAsync());
            Assert.Contains("Normalized email duplicates",error.MessageText);
            Assert.Equal(2L,await new NpgsqlCommand("SELECT count(*) FROM users",connection).ExecuteScalarAsync());
            Assert.Equal(2L,await new NpgsqlCommand("SELECT count(*) FROM users WHERE lower(btrim(email))='admin@example.test'",connection).ExecuteScalarAsync());
            Assert.Equal(0L,await new NpgsqlCommand("SELECT count(*) FROM public.\"__EFMigrationsHistory\" WHERE \"MigrationId\"='20261003030000_AddNormalizedRegistrationEmail'",connection).ExecuteScalarAsync());
        }
        finally {NpgsqlConnection.ClearAllPools();await new NpgsqlCommand($"DROP DATABASE \"{legacyName}\" WITH (FORCE)",admin).ExecuteNonQueryAsync();}
    }

    [PostgresFact]
    public async Task Request_and_resend_reject_existing_email_before_creating_a_delivery_job()
    {
        // Includes a legacy mixed-case stored address and a blocked account.
        await ExecuteAsync("UPDATE users SET email='Admin@Example.Test',is_active=false WHERE email='admin@example.test'");
        foreach (var route in new[] { "/api/auth/registration/request-otp", "/api/auth/resend-verification" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route)
            {
                Content = JsonContent.Create(new { email = "  ADMIN@EXAMPLE.TEST  " })
            };
            request.Headers.Accept.ParseAdd("text/plain");
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("EMAIL_EXISTS", error.GetProperty("code").GetString());
            Assert.NotEmpty(error.GetProperty("errors").GetProperty("email").EnumerateArray());
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("traceId").GetString()));
        }
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM registration_otp_email_jobs"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM registration_email_challenges"));
    }

    [PostgresFact]
    public async Task Registration_email_conflict_rolls_back_organization_and_proof_consumption()
    {
        const string email = "legacy-owner@example.test";
        var proof = await VerifyRegistrationEmailAsync(email);
        // Another account wins after OTP verification, before the final form submission.
        await ExecuteAsync("UPDATE users SET email='Legacy-Owner@Example.Test' WHERE email='admin@example.test'");
        var response = await client.PostAsJsonAsync("/api/auth/register/organization", new
        {
            email, password = "Test123", confirmPassword = "Test123", registrationToken = proof,
            organizationName = "No orphan", organizationAddress = "1 Test Street", organizationPhoneNumber = "0706364866"
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EMAIL_EXISTS", error.GetProperty("code").GetString());
        Assert.NotEmpty(error.GetProperty("errors").GetProperty("email").EnumerateArray());
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM organizations WHERE name='No orphan'"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM registration_email_challenges WHERE registration_token_used_at IS NOT NULL"));
    }

    [PostgresFact]
    public async Task Invalid_final_form_does_not_consume_proof_and_corrected_form_keeps_all_fields()
    {
        const string email = "form@example.test";
        var proof = await VerifyRegistrationEmailAsync(email);
        var form = new
        {
            email, username = "FORM.User", password = "Test123", confirmPassword = "wrong",
            fullName = "Test User", dob = "2004-07-29", gender = "Other", phoneNumber = "0706364866", registrationToken = proof
        };
        var invalid = await client.PostAsJsonAsync("/api/auth/register/trainee", form);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var error = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(error.GetProperty("errors").TryGetProperty("confirmPassword", out _));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM users WHERE email='form@example.test'"));
        var created = await client.PostAsJsonAsync("/api/auth/register/trainee", form with { confirmPassword = "Test123" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("form.user", await ScalarAsync("SELECT username FROM users WHERE email='form@example.test'"));
        Assert.Equal("2004-07-29", await ScalarAsync("SELECT dob::text FROM users WHERE email='form@example.test'"));
        Assert.Equal("Other", await ScalarAsync("SELECT gender::text FROM users WHERE email='form@example.test'"));
        Assert.Equal("0706364866", await ScalarAsync("SELECT phone_number FROM users WHERE email='form@example.test'"));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM users WHERE email='form@example.test' AND email_verified_at IS NOT NULL"));
    }

    [PostgresFact]
    public async Task Final_form_requests_compete_for_one_proof_without_creating_two_accounts()
    {
        const string email = "race-form@example.test";
        var proof = await VerifyRegistrationEmailAsync(email);
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => client.PostAsJsonAsync("/api/auth/register/trainee", new
        {
            email, username = "race-form-" + i, password = "Test123", confirmPassword = "Test123", registrationToken = proof
        })));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.BadRequest);
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM users WHERE email='race-form@example.test'"));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM registration_email_challenges WHERE registration_token_used_at IS NOT NULL"));
    }
}
