using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Infrastructure.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class PersonalPhoneTests
{
    internal static Task Apply(BillingDatabase db) => db.Sql("BEGIN;\n" +
        string.Join("\n", new AddPersonalPhoneUniqueness().UpOperations.OfType<SqlOperation>().Select(x => x.Sql)) + "\nCOMMIT;");

    private static async Task Prepare(BillingDatabase db)
    {
        await OrganizationPhoneHttpTests.Prepare(db);
        await Apply(db);
    }

    private static Task<HttpResponseMessage> Register(HttpClient client, bool google, bool organization, string id, string proof, string? phone, string organizationPhone)
    {
        var body = new Dictionary<string, object?> { ["phoneNumber"] = phone };
        if (google) { body["onboardingToken"] = proof; body["accountType"] = organization ? "organization" : "trainee"; }
        else
        {
            body["email"] = id + "@example.test"; body["password"] = "123456";
            body["confirmPassword"] = "123456"; body["fullName"] = "Test User"; body["registrationToken"] = proof;
        }
        if (organization)
        { body["organizationName"] = id; body["organizationAddress"] = "Test address"; body["organizationPhoneNumber"] = organizationPhone; }
        else body["username"] = id;
        return client.PostAsJsonAsync(google ? "/api/auth/google/onboarding/complete" : organization ? "/api/auth/register/organization" : "/api/auth/register/trainee", body);
    }

    private static async Task Conflict(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, text);
        using var body = JsonDocument.Parse(text);
        Assert.Equal("PHONE_NUMBER_EXISTS", body.RootElement.GetProperty("code").GetString());
        Assert.Single(body.RootElement.GetProperty("errors").GetProperty("phoneNumber").EnumerateArray());
        Assert.True(body.RootElement.TryGetProperty("traceId", out _));
        Assert.False(body.RootElement.TryGetProperty("userId", out _));
    }

    [BillingPostgresFact]
    public async Task Personal_phone_conflict_across_email_google_and_both_roles_rolls_back_and_proof_can_retry()
    {
        foreach (var organization in new[] { false, true })
        foreach (var (firstGoogle, secondGoogle) in new[] { (false, false), (true, true), (false, true), (true, false) })
        {
            await using var db = await BillingDatabase.Create(false); await Prepare(db);
            using var factory = OrganizationPhoneHttpTests.Factory(db); using var client = factory.CreateClient();
            var a = await OrganizationPhoneHttpTests.Proof(factory, client, firstGoogle, "phone-first");
            var first = await Register(client, firstGoogle, organization, "phone-first", a, "0706364866", "0706364866");
            Assert.True(first.StatusCode == HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
            var b = await OrganizationPhoneHttpTests.Proof(factory, client, secondGoogle, "phone-second");
            var before = await db.Scalar("SELECT jsonb_build_array((SELECT count(*) FROM users),(SELECT count(*) FROM organizations),(SELECT count(*) FROM auth_refresh_tokens),(SELECT count(*) FROM audit_logs))::text");
            await Conflict(await Register(client, secondGoogle, organization, "phone-second", b, " (070) 636-4866 ", "0706364867"));
            Assert.Equal(before, await db.Scalar("SELECT jsonb_build_array((SELECT count(*) FROM users),(SELECT count(*) FROM organizations),(SELECT count(*) FROM auth_refresh_tokens),(SELECT count(*) FROM audit_logs))::text"));
            Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM registration_email_challenges WHERE email='phone-second@example.test' AND registration_token_used_at IS NOT NULL"));
            Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM auth_google_onboarding_sessions WHERE firebase_uid='phone-second' AND completed_at IS NOT NULL"));
            var retry = await Register(client, secondGoogle, organization, "phone-second", b, "0706364867", "0706364867");
            Assert.True(retry.StatusCode == HttpStatusCode.Created, await retry.Content.ReadAsStringAsync());
        }
    }

    [BillingPostgresFact]
    public async Task Concurrent_registration_has_one_winner_and_loser_proof_remains_usable()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db);
        using var factory = OrganizationPhoneHttpTests.Factory(db); using var a = factory.CreateClient(); using var b = factory.CreateClient();
        var pa = await OrganizationPhoneHttpTests.Proof(factory, a, false, "race-phone-a");
        var pb = await OrganizationPhoneHttpTests.Proof(factory, b, true, "race-phone-b");
        var responses = await Task.WhenAll(Register(a, false, true, "race-phone-a", pa, "0706364866", "0706364867"),
            Register(b, true, true, "race-phone-b", pb, "(070) 636-4866", "0706364868"));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var loser = Array.FindIndex(responses, r => r.StatusCode != HttpStatusCode.Created); await Conflict(responses[loser]);
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM users WHERE phone_number='0706364866'"));
        var retry = loser == 0 ? await Register(a, false, true, "race-phone-a", pa, "0706364869", "0706364867")
            : await Register(b, true, true, "race-phone-b", pb, "0706364869", "0706364868");
        Assert.True(retry.StatusCode == HttpStatusCode.Created, await retry.Content.ReadAsStringAsync());
    }

    [BillingPostgresFact]
    public async Task Personal_profile_conflict_keeps_revision_audit_and_supports_own_number_omitted_and_null()
    {
        await using var db = await BillingDatabase.Create(false); await Prepare(db);
        await db.Sql($"UPDATE users SET phone_number='0706364866' WHERE id='{BillingDatabase.Owner}';UPDATE users SET phone_number='+84706364866',is_active=false,deleted_at=now() WHERE id='{BillingDatabase.Other}'");
        using var factory = OrganizationPhoneHttpTests.Factory(db); using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Test-Actor", BillingDatabase.Owner.ToString());
        async Task<HttpResponseMessage> Patch(string? tag, object body)
        { var request = new HttpRequestMessage(HttpMethod.Patch, "/api/auth/me") { Content = JsonContent.Create(body) }; if(tag is not null) request.Headers.TryAddWithoutValidation("If-Match", tag); return await client.SendAsync(request); }
        var get = await client.GetAsync("/api/auth/me"); Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var own = await Patch(get.Headers.ETag!.ToString(), new { phoneNumber = "(070) 636-4866" }); Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var tag = own.Headers.ETag!.ToString(); var audit = await db.Scalar("SELECT count(*) FROM audit_logs");
        await Conflict(await Patch(tag, new { phoneNumber = "+84 706-364-866" }));
        Assert.Equal(audit, await db.Scalar("SELECT count(*) FROM audit_logs")); Assert.Equal(tag, (await client.GetAsync("/api/auth/me")).Headers.ETag!.ToString());
        Assert.Equal((HttpStatusCode)428, (await Patch(null, new { phoneNumber = "0706364867" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch("invalid", new { phoneNumber = "0706364867" })).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Patch(get.Headers.ETag!.ToString(), new { phoneNumber = "0706364867" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(tag, new { phoneNumber = "abc123456" })).StatusCode);
        var omitted = await Patch(tag, new { fullName = "Updated Name" }); Assert.Equal(HttpStatusCode.OK, omitted.StatusCode);
        Assert.Equal("0706364866", await db.Scalar($"SELECT phone_number FROM users WHERE id='{BillingDatabase.Owner}'"));
        Assert.Equal(HttpStatusCode.OK, (await Patch(omitted.Headers.ETag!.ToString(), new { phoneNumber = (string?)null })).StatusCode);
        Assert.Equal(true, await db.Scalar($"SELECT phone_number IS NULL FROM users WHERE id='{BillingDatabase.Owner}'"));
    }

    [BillingPostgresFact]
    public async Task Migration_preserves_data_and_nulls_but_refuses_duplicate_or_invalid_legacy_values()
    {
        foreach (var value in new string?[] { null, "+84706364866", "(070) 636-4866", "abc123456", "" })
        {
            await using var db = await BillingDatabase.Create(false);
            await using var connection = new NpgsqlConnection(db.Connection); await connection.OpenAsync();
            using var seed = new NpgsqlCommand($"UPDATE users SET phone_number=' (070) 636-4866 ',is_active=false,deleted_at=now() WHERE id='{BillingDatabase.Owner}';UPDATE users SET phone_number=@phone WHERE id='{BillingDatabase.Other}'", connection);
            seed.Parameters.AddWithValue("phone", NpgsqlTypes.NpgsqlDbType.Text, (object?)value ?? DBNull.Value); await seed.ExecuteNonQueryAsync();
            var before = await db.Scalar("SELECT jsonb_agg(to_jsonb(u) ORDER BY id)::text FROM users u");
            if (value is null or "+84706364866")
            {
                await Apply(db);
                var error = await Assert.ThrowsAsync<PostgresException>(() => db.Sql($"UPDATE users SET phone_number='0706364866' WHERE id='{BillingDatabase.Other}'"));
                Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState); Assert.Equal("users_phone_normalized_key", error.ConstraintName);
            }
            else
            { var error = await Assert.ThrowsAsync<PostgresException>(() => Apply(db)); Assert.Contains("Personal phone preflight failed", error.MessageText); Assert.Equal(true, await db.Scalar("SELECT to_regclass('public.users_phone_normalized_key') IS NULL")); }
            Assert.Equal(before, await db.Scalar("SELECT jsonb_agg(to_jsonb(u) ORDER BY id)::text FROM users u"));
        }
    }
}
