using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class PhoneProfileMutationTests
{
    private static string Route(bool organization) => organization ? "/api/organizations/me" : "/api/auth/me";
    private static string Table(bool organization) => organization ? "organizations" : "users";
    private static string Column(bool organization) => organization ? "phone" : "phone_number";
    private static Guid Id(bool organization, bool other = false) => organization
        ? other ? BillingDatabase.OtherOrg : BillingDatabase.Org
        : other ? BillingDatabase.Other : BillingDatabase.Owner;

    private static async Task Prepare(BillingDatabase db)
    {
        await OrganizationPhoneHttpTests.Prepare(db);
        await PersonalPhoneTests.Apply(db);
        await db.Sql($"UPDATE users SET phone_number=CASE WHEN id='{BillingDatabase.Owner}' THEN '0706364866' ELSE '0706364867' END WHERE id IN ('{BillingDatabase.Owner}','{BillingDatabase.Other}');" +
            $"UPDATE organizations SET phone=CASE WHEN id='{BillingDatabase.Org}' THEN '0706364866' ELSE '0706364867' END WHERE id IN ('{BillingDatabase.Org}','{BillingDatabase.OtherOrg}')");
    }

    private static Task<object?> Snapshot(BillingDatabase db, bool organization, bool other = false) =>
        db.Scalar($"SELECT to_jsonb(p)::text FROM {Table(organization)} p WHERE id='{Id(organization, other)}'");

    private static async Task<string> Etag(HttpClient client, bool organization)
    {
        var response = await client.GetAsync(Route(organization));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return response.Headers.ETag!.ToString();
    }

    private static Task<HttpResponseMessage> Patch(HttpClient client, bool organization, string tag, string phone)
    {
        // Include another field to prove that a phone conflict rolls back the entire edit.
        object input = organization ? new { phoneNumber = phone, name = "Edited organization", address = "Edited address" }
            : new { phoneNumber = phone, fullName = "Edited user", dob = "2000-01-02" };
        var request = new HttpRequestMessage(HttpMethod.Patch, Route(organization)) { Content = JsonContent.Create(input) };
        request.Headers.TryAddWithoutValidation("If-Match", tag);
        return client.SendAsync(request);
    }

    private static async Task Conflict(HttpResponseMessage response, bool organization, string field = "phoneNumber")
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, text);
        using var body = JsonDocument.Parse(text);
        Assert.Equal(organization ? "ORGANIZATION_PHONE_EXISTS" : "PHONE_NUMBER_EXISTS", body.RootElement.GetProperty("code").GetString());
        Assert.Single(body.RootElement.GetProperty("errors").GetProperty(field).EnumerateArray());
        Assert.True(body.RootElement.TryGetProperty("traceId", out _));
    }

    private static HttpClient As(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, bool other = false)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Actor", (other ? BillingDatabase.Other : BillingDatabase.Owner).ToString());
        return client;
    }

    [BillingPostgresFact]
    public async Task Phone_edit_conflict_rolls_back_all_fields_and_keeps_own_number_for_active_inactive_and_deleted_holders()
    {
        foreach (var organization in new[] { false, true })
        foreach (var lifecycle in new[] { "active", "inactive", "deleted" })
        {
            await using var db = await BillingDatabase.Create(false); await Prepare(db);
            await db.Sql($"UPDATE {Table(organization)} SET is_active={(lifecycle == "active" ? "true" : "false")},deleted_at={(lifecycle == "deleted" ? "now()" : "NULL")} WHERE id='{Id(organization, true)}'");
            using var factory = OrganizationPhoneHttpTests.Factory(db); using var client = As(factory);
            var tag = await Etag(client, organization); var before = await Snapshot(db, organization);
            var audits = await db.Scalar("SELECT count(*) FROM audit_logs");
            await Conflict(await Patch(client, organization, tag, " (070) 636-4867 "), organization);
            Assert.Equal(before, await Snapshot(db, organization));
            Assert.Equal(audits, await db.Scalar("SELECT count(*) FROM audit_logs"));
            Assert.Equal(tag, await Etag(client, organization));
            var own = await Patch(client, organization, tag, " (070) 636-4866 ");
            Assert.True(own.StatusCode == HttpStatusCode.OK, await own.Content.ReadAsStringAsync());
            Assert.Equal("0706364866", await db.Scalar($"SELECT {Column(organization)} FROM {Table(organization)} WHERE id='{Id(organization)}'"));
        }
    }

    [BillingPostgresFact]
    public async Task Two_profiles_racing_for_one_number_have_one_winner_and_loser_can_retry_unchanged_etag()
    {
        foreach (var organization in new[] { false, true })
        {
            await using var db = await BillingDatabase.Create(false); await Prepare(db);
            using var factory = OrganizationPhoneHttpTests.Factory(db); using var a = As(factory); using var b = As(factory, true);
            var tags = new[] { await Etag(a, organization), await Etag(b, organization) };
            var snapshots = new[] { await Snapshot(db, organization), await Snapshot(db, organization, true) };
            var audits = (long)(await db.Scalar("SELECT count(*) FROM audit_logs"))!;
            var responses = await Task.WhenAll(Patch(a, organization, tags[0], "0706364800"), Patch(b, organization, tags[1], "(070) 636-4800"));
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            var loser = Array.FindIndex(responses, r => r.StatusCode != HttpStatusCode.OK);
            await Conflict(responses[loser], organization);
            Assert.Equal(snapshots[loser], await Snapshot(db, organization, loser == 1));
            Assert.Equal(tags[loser], await Etag(loser == 0 ? a : b, organization));
            Assert.Equal(audits + 1, await db.Scalar("SELECT count(*) FROM audit_logs"));
            Assert.Equal(1L, await db.Scalar($"SELECT count(*) FROM {Table(organization)} WHERE regexp_replace({Column(organization)},'[^0-9+]','','g')='0706364800'"));
            var retry = await Patch(loser == 0 ? a : b, organization, tags[loser], "0706364801");
            Assert.True(retry.StatusCode == HttpStatusCode.OK, await retry.Content.ReadAsStringAsync());
        }
    }

    [BillingPostgresFact]
    public async Task Registration_and_profile_edit_race_for_one_phone_without_partial_accounts_or_consumed_loser_proof()
    {
        foreach (var organization in new[] { false, true })
        foreach (var google in new[] { false, true })
        {
            await using var db = await BillingDatabase.Create(false); await Prepare(db);
            using var factory = OrganizationPhoneHttpTests.Factory(db); using var profile = As(factory); using var registration = factory.CreateClient();
            var proof = await OrganizationPhoneHttpTests.Proof(factory, registration, google, "edit-race-new");
            var tag = await Etag(profile, organization); var before = await Snapshot(db, organization);
            var counts = await db.Scalar("SELECT jsonb_build_array((SELECT count(*) FROM users),(SELECT count(*) FROM organizations),(SELECT count(*) FROM auth_refresh_tokens))::text");
            var responses = await Task.WhenAll(Patch(profile, organization, tag, "0706364800"),
                OrganizationPhoneHttpTests.Register(registration, google, "edit-race-new", proof, "(070) 636-4800"));
            var registrationWon = responses[1].StatusCode == HttpStatusCode.Created;
            Assert.True(registrationWon != (responses[0].StatusCode == HttpStatusCode.OK),
                string.Join("\n", await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()))));
            Assert.Equal(1L, await db.Scalar($"SELECT count(*) FROM {Table(organization)} WHERE regexp_replace({Column(organization)},'[^0-9+]','','g')='0706364800'"));
            if (registrationWon)
            {
                await Conflict(responses[0], organization);
                Assert.Equal(before, await Snapshot(db, organization));
                Assert.Equal(tag, await Etag(profile, organization));
                Assert.Equal(HttpStatusCode.OK, (await Patch(profile, organization, tag, "0706364801")).StatusCode);
            }
            else
            {
                await Conflict(responses[1], organization, organization ? "organizationPhoneNumber" : "phoneNumber");
                Assert.Equal(counts, await db.Scalar("SELECT jsonb_build_array((SELECT count(*) FROM users),(SELECT count(*) FROM organizations),(SELECT count(*) FROM auth_refresh_tokens))::text"));
                var retry = await OrganizationPhoneHttpTests.Register(registration, google, "edit-race-new", proof, "0706364801");
                Assert.True(retry.StatusCode == HttpStatusCode.Created, await retry.Content.ReadAsStringAsync());
            }
        }
    }

    [BillingPostgresFact]
    public async Task Same_profile_concurrent_edits_reject_stale_etag_without_overwriting_winner()
    {
        foreach (var organization in new[] { false, true })
        {
            await using var db = await BillingDatabase.Create(false); await Prepare(db);
            using var factory = OrganizationPhoneHttpTests.Factory(db); using var a = As(factory); using var b = As(factory);
            var tag = await Etag(a, organization); var audits = (long)(await db.Scalar("SELECT count(*) FROM audit_logs"))!;
            var phones = new[] { "0706364800", "0706364801" };
            var responses = await Task.WhenAll(Patch(a, organization, tag, phones[0]), Patch(b, organization, tag, phones[1]));
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            var winner = Array.FindIndex(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Equal(HttpStatusCode.PreconditionFailed, responses[1 - winner].StatusCode);
            Assert.Equal(phones[winner], await db.Scalar($"SELECT {Column(organization)} FROM {Table(organization)} WHERE id='{Id(organization)}'"));
            Assert.Equal(audits + 1, await db.Scalar("SELECT count(*) FROM audit_logs"));
            Assert.Equal(responses[winner].Headers.ETag!.ToString(), await Etag(a, organization));
        }
    }

    [BillingPostgresFact]
    public async Task Audit_failure_rolls_back_phone_edit_and_old_number_is_released_only_after_successful_commit()
    {
        foreach (var organization in new[] { false, true })
        {
            await using var db = await BillingDatabase.Create(false); await Prepare(db);
            using var factory = OrganizationPhoneHttpTests.Factory(db); using var a = As(factory); using var b = As(factory, true);
            var aTag = await Etag(a, organization); var bTag = await Etag(b, organization);
            var before = await Snapshot(db, organization); var audits = await db.Scalar("SELECT count(*) FROM audit_logs");
            await db.Sql("CREATE FUNCTION fail_phone_edit_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture audit rollback' USING ERRCODE='23514'; END $$; CREATE TRIGGER fail_phone_edit BEFORE INSERT ON audit_logs FOR EACH ROW EXECUTE FUNCTION fail_phone_edit_audit()");
            Assert.Equal(HttpStatusCode.InternalServerError, (await Patch(a, organization, aTag, "0706364800")).StatusCode);
            Assert.Equal(before, await Snapshot(db, organization));
            Assert.Equal(audits, await db.Scalar("SELECT count(*) FROM audit_logs"));
            Assert.Equal(aTag, await Etag(a, organization));
            await Conflict(await Patch(b, organization, bTag, "(070) 636-4866"), organization);
            await db.Sql("DROP TRIGGER fail_phone_edit ON audit_logs");
            Assert.Equal(HttpStatusCode.OK, (await Patch(a, organization, aTag, "0706364800")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Patch(b, organization, bTag, "(070) 636-4866")).StatusCode);
            Assert.Equal("0706364866", await db.Scalar($"SELECT {Column(organization)} FROM {Table(organization)} WHERE id='{Id(organization, true)}'"));
        }
    }
}
