using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.API.Extensions;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Application.Storage;
using Fire3D.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class OrganizationPhoneHttpTests
{
    [BillingPostgresFact]
    public async Task Duplicate_phone_across_email_and_google_rolls_back_and_preserves_proof_for_retry()
    {
        foreach (var (firstGoogle, secondGoogle) in new[] { (false, false), (true, true), (false, true), (true, false) })
        {
            await using var db = await BillingDatabase.Create(false);
            await Prepare(db);
            using var factory = Factory(db);
            using var client = factory.CreateClient();
            var firstProof = await Proof(factory, client, firstGoogle, "org-first");
            Assert.Equal(HttpStatusCode.Created, (await Register(client, firstGoogle, "org-first", firstProof, "0706364866")).StatusCode);
            var secondProof = await Proof(factory, client, secondGoogle, "org-second");
            var users = await db.Scalar("SELECT count(*) FROM users");
            var organizations = await db.Scalar("SELECT count(*) FROM organizations");
            var sessions = await db.Scalar("SELECT count(*) FROM auth_refresh_tokens");
            var audits = await db.Scalar("SELECT count(*) FROM audit_logs");

            await PhoneConflict(await Register(client, secondGoogle, "org-second", secondProof, " (070) 636-4866 "), "organizationPhoneNumber");
            Assert.Equal(users, await db.Scalar("SELECT count(*) FROM users"));
            Assert.Equal(organizations, await db.Scalar("SELECT count(*) FROM organizations"));
            Assert.Equal(sessions, await db.Scalar("SELECT count(*) FROM auth_refresh_tokens"));
            Assert.Equal(audits, await db.Scalar("SELECT count(*) FROM audit_logs"));
            Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM registration_email_challenges WHERE email='org-second@example.test' AND registration_token_used_at IS NOT NULL"));
            Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM auth_google_onboarding_sessions WHERE firebase_uid='org-second' AND completed_at IS NOT NULL"));
            var retry = await Register(client, secondGoogle, "org-second", secondProof, "0706364867");
            Assert.True(retry.StatusCode == HttpStatusCode.Created, await retry.Content.ReadAsStringAsync());
        }
    }

    [BillingPostgresFact]
    public async Task Patch_keeps_own_number_but_conflict_does_not_change_revision_or_audit()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db);
        await db.Sql($"UPDATE organizations SET phone='0706364866' WHERE id='{BillingDatabase.Org}'; UPDATE organizations SET phone='+84706364866' WHERE id='{BillingDatabase.OtherOrg}';");
        using var factory = Factory(db); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Actor", BillingDatabase.Owner.ToString());
        var get = await client.GetAsync("/api/organizations/me");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var own = await Patch(client, get.Headers.ETag!.ToString(), new { phoneNumber = "(070) 636-4866" });
        Assert.True(own.StatusCode == HttpStatusCode.OK, await own.Content.ReadAsStringAsync());
        var currentEtag = own.Headers.ETag!.ToString();
        var audits = await db.Scalar("SELECT count(*) FROM audit_logs");
        await PhoneConflict(await Patch(client, currentEtag, new { phoneNumber = "+84 706-364-866" }), "phoneNumber");
        Assert.Equal(audits, await db.Scalar("SELECT count(*) FROM audit_logs"));
        var after = await client.GetAsync("/api/organizations/me");
        Assert.Equal(currentEtag, after.Headers.ETag!.ToString());
        Assert.Equal("0706364866", await db.Scalar($"SELECT phone FROM organizations WHERE id='{BillingDatabase.Org}'"));
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Patch(client, get.Headers.ETag!.ToString(), new { phoneNumber = "+84706364866" })).StatusCode);
        Assert.Equal((HttpStatusCode)428, (await Patch(client, null, new { phoneNumber = "0706364868" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(client, "invalid", new { phoneNumber = "0706364868" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(client, currentEtag, new { phoneNumber = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(client, currentEtag, new { phoneNumber = "invalid" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Patch(client, currentEtag, new { address = "New address" })).StatusCode);
    }

    [BillingPostgresFact]
    public async Task Inactive_and_deleted_organizations_keep_their_numbers_and_personal_phone_is_separate()
    {
        foreach (var deleted in new[] { false, true })
        {
            await using var db = await BillingDatabase.Create(false); await Prepare(db);
            await db.Sql($"UPDATE organizations SET phone='0706364866',is_active=false,deleted_at={(deleted ? "now()" : "NULL")} WHERE id='{BillingDatabase.OtherOrg}'");
            using var factory = Factory(db); using var client = factory.CreateClient();
            var proof = await Proof(factory, client, false, "new-org");
            await PhoneConflict(await Register(client, false, "new-org", proof, "0706364866"), "organizationPhoneNumber");
            Assert.Equal(HttpStatusCode.Created, (await Register(client, false, "new-org", proof, "+84706364866")).StatusCode);
            Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM users WHERE email='new-org@example.test' AND phone_number='+84706364866'"));
        }
    }

    [BillingPostgresFact]
    public async Task Invalid_phone_does_not_consume_registration_or_google_proof()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db);
        using var factory = Factory(db); using var client = factory.CreateClient();
        foreach (var google in new[] { false, true })
        {
            var id = google ? "invalid-google" : "invalid-email";
            var proof = await Proof(factory, client, google, id);
            Assert.Equal(HttpStatusCode.BadRequest, (await Register(client, google, id, proof, "123abc456")).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await Register(client, google, id, proof, google ? "0706364867" : "0706364866")).StatusCode);
        }
    }

    internal static async Task Prepare(BillingDatabase db)
    {
        await GoogleOnboardingPostgresTests.Prepare(db);
        await BackendDatabasePermissionsTests.Apply(db, "AddPreRegistrationOtp");
        await db.Sql("GRANT UPDATE ON auth_refresh_tokens TO fire3d_api;");
        // Target-schema fixture for conflict mapping; migration execution is covered separately.
        await db.Sql("CREATE UNIQUE INDEX organizations_phone_normalized_key ON public.organizations (regexp_replace(phone,'[^0-9+]','','g')) WHERE phone IS NOT NULL");
    }

    internal static WebApplicationFactory<Program> Factory(BillingDatabase db)
    {
        var runtime = new NpgsqlConnectionStringBuilder(db.Connection) { Options = "-c role=fire3d_api" }.ConnectionString;
        return new BillingApiTests.Factory(db).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<Fire3DDbContext>();
            services.RemoveAll<DbContextOptions<Fire3DDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<Fire3DDbContext>>();
            services.AddDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:DefaultConnection"] = runtime }).Build());
            services.RemoveAll<IIdentityProvider>();
            services.AddSingleton(ResetProxy.For<IIdentityProvider>((_, args) =>
                Task.FromResult(new VerifiedIdentity((string)args![0]!, args[0] + "@example.test", "Verified Name"))));
            services.RemoveAll<IStorageService>();
            services.AddSingleton(ResetProxy.For<IStorageService>((_, _) => throw new InvalidOperationException("Phone tests must not call S3")));
            services.AddExceptionHandler<LoginAuditPermissionsPostgresTests.TestDatabaseFailureHandler>();
        }));
    }

    internal static async Task<string> Proof(WebApplicationFactory<Program> factory, HttpClient client, bool google, string id)
    {
        if (google)
        {
            var exchange = await client.PostAsJsonAsync("/api/auth/login-firebase", id);
            Assert.True(exchange.StatusCode == HttpStatusCode.OK, await exchange.Content.ReadAsStringAsync());
            using var body = JsonDocument.Parse(await exchange.Content.ReadAsStringAsync());
            return body.RootElement.GetProperty("onboarding").GetProperty("token").GetString()!;
        }
        var email = id + "@example.test";
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/auth/registration/request-otp", new { email })).StatusCode);
        using var scope = factory.Services.CreateScope();
        using var connection = new NpgsqlConnection(scope.ServiceProvider.GetRequiredService<Fire3DDbContext>().Database.GetConnectionString());
        await connection.OpenAsync();
        Assert.Equal("fire3d_api", await new NpgsqlCommand("SELECT current_user", connection).ExecuteScalarAsync());
        var job = await scope.ServiceProvider.GetRequiredService<IRegistrationOtpDeliveryQueue>().ClaimAsync(default);
        Assert.NotNull(job); Assert.Equal(email, job.Email);
        var verified = await client.PostAsJsonAsync("/api/auth/registration/verify-otp", new { email, otp = job.Otp });
        Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
        return (await verified.Content.ReadFromJsonAsync<RegistrationOtpVerificationResponse>())!.RegistrationToken;
    }

    internal static Task<HttpResponseMessage> Register(HttpClient client, bool google, string id, string proof, string phone)
        => google
            ? client.PostAsJsonAsync("/api/auth/google/onboarding/complete", new
            { onboardingToken = proof, accountType = "organization", organizationName = id, organizationAddress = "Test address", organizationPhoneNumber = phone, phoneNumber = phone })
            : client.PostAsJsonAsync("/api/auth/register/organization", new
            { email = id + "@example.test", password = "123456", confirmPassword = "123456", fullName = "Test Owner", organizationName = id, organizationAddress = "Test address", organizationPhoneNumber = phone, phoneNumber = phone, registrationToken = proof });

    internal static async Task PhoneConflict(HttpResponseMessage response, string field)
    {
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, content);
        using var body = JsonDocument.Parse(content);
        Assert.Equal("ORGANIZATION_PHONE_EXISTS", body.RootElement.GetProperty("code").GetString());
        Assert.Single(body.RootElement.GetProperty("errors").GetProperty(field).EnumerateArray());
        Assert.True(body.RootElement.TryGetProperty("traceId", out _));
        Assert.False(body.RootElement.TryGetProperty("organizationId", out _));
    }

    internal static Task<HttpResponseMessage> Patch(HttpClient client, string? etag, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, "/api/organizations/me") { Content = JsonContent.Create(body) };
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return client.SendAsync(request);
    }
}
