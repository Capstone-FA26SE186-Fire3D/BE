using System.Net;
using Fire3D.Application.Authentication;
using Fire3D.Domain.Entities;
using Fire3D.Domain.Enums;
using Fire3D.Infrastructure.Authentication;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Fire3D.AuthTests;

public sealed class OrganizationPhoneConcurrencyTests
{
    [BillingPostgresFact]
    public async Task Concurrent_registrations_across_both_flows_create_at_most_one_organization()
    {
        foreach (var (firstGoogle, secondGoogle) in new[] { (false, false), (true, true), (false, true) })
        {
            await using var db = await BillingDatabase.Create(false); await OrganizationPhoneHttpTests.Prepare(db);
            using var factory = OrganizationPhoneHttpTests.Factory(db);
            using var first = factory.CreateClient(); using var second = factory.CreateClient();
            var proof1 = await OrganizationPhoneHttpTests.Proof(factory, first, firstGoogle, "concurrent-one");
            var proof2 = await OrganizationPhoneHttpTests.Proof(factory, second, secondGoogle, "concurrent-two");
            // Overlap the write windows on separate request connections without changing production code.
            await db.Sql("CREATE FUNCTION phone_test_delay() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_sleep(0.15); RETURN NEW; END $$; CREATE TRIGGER phone_test_delay BEFORE INSERT ON organizations FOR EACH ROW EXECUTE FUNCTION phone_test_delay();");
            var results = await Task.WhenAll(
                OrganizationPhoneHttpTests.Register(first, firstGoogle, "concurrent-one", proof1, "0706364866"),
                OrganizationPhoneHttpTests.Register(second, secondGoogle, "concurrent-two", proof2, "(070) 636-4866"));
            Assert.Single(results, x => x.StatusCode == HttpStatusCode.Created);
            await OrganizationPhoneHttpTests.PhoneConflict(Assert.Single(results, x => x.StatusCode != HttpStatusCode.Created), "organizationPhoneNumber");
            Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM organizations"));
            Assert.Equal(4L, await db.Scalar("SELECT count(*) FROM users"));
            Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM audit_logs WHERE action='Create'"));
            var googleWon = results[0].StatusCode == HttpStatusCode.Created ? firstGoogle : secondGoogle;
            Assert.Equal(googleWon ? 4L : 3L, await db.Scalar("SELECT count(*) FROM auth_refresh_tokens"));
            Assert.Equal(1L, await db.Scalar("SELECT (SELECT count(*) FROM auth_google_onboarding_sessions WHERE completed_at IS NOT NULL)+(SELECT count(*) FROM registration_email_challenges WHERE registration_token_used_at IS NOT NULL)"));
        }
    }

    [BillingPostgresFact]
    public async Task Patch_and_registration_cannot_claim_the_same_phone_or_leave_partial_audits()
    {
        await using var db = await BillingDatabase.Create(false); await OrganizationPhoneHttpTests.Prepare(db);
        using var factory = OrganizationPhoneHttpTests.Factory(db); using var registration = factory.CreateClient(); using var profile = factory.CreateClient();
        profile.DefaultRequestHeaders.Add("X-Test-Actor", BillingDatabase.Owner.ToString());
        var get = await profile.GetAsync("/api/organizations/me");
        var proof = await OrganizationPhoneHttpTests.Proof(factory, registration, false, "competing-register");
        var outcomes = await Task.WhenAll(
            OrganizationPhoneHttpTests.Register(registration, false, "competing-register", proof, "0706364866"),
            OrganizationPhoneHttpTests.Patch(profile, get.Headers.ETag!.ToString(), new { phoneNumber = "(070) 636-4866" }));
        Assert.Single(outcomes, x => x.IsSuccessStatusCode);
        var registerWon = outcomes[0].StatusCode == HttpStatusCode.Created;
        await OrganizationPhoneHttpTests.PhoneConflict(outcomes[registerWon ? 1 : 0], registerWon ? "phoneNumber" : "organizationPhoneNumber");
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM organizations WHERE phone='0706364866'"));
        Assert.Equal(registerWon ? 3L : 2L, await db.Scalar("SELECT count(*) FROM organizations"));
        Assert.Equal(registerWon ? 4L : 3L, await db.Scalar("SELECT count(*) FROM users"));
        Assert.Equal(registerWon ? 1L : 2L, await db.Scalar($"SELECT profile_revision FROM organizations WHERE id='{BillingDatabase.Org}'"));
        Assert.Equal(1L, await db.Scalar("SELECT count(*) FROM audit_logs"));
    }

    [BillingPostgresFact]
    public async Task Audit_failure_rolls_back_phone_revision_and_registration_proof()
    {
        await using var db = await BillingDatabase.Create(false); await OrganizationPhoneHttpTests.Prepare(db);
        await db.Sql("ALTER TABLE audit_logs ADD CONSTRAINT reject_phone_audit CHECK(false) NOT VALID");
        using var factory = OrganizationPhoneHttpTests.Factory(db); using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Actor", BillingDatabase.Owner.ToString());
        var get = await client.GetAsync("/api/organizations/me");
        Assert.Equal(HttpStatusCode.InternalServerError, (await OrganizationPhoneHttpTests.Patch(client, get.Headers.ETag!.ToString(), new { phoneNumber = "0706364866" })).StatusCode);
        Assert.Equal(get.Headers.ETag!.ToString(), (await client.GetAsync("/api/organizations/me")).Headers.ETag!.ToString());
        Assert.Equal(1L, await db.Scalar($"SELECT profile_revision FROM organizations WHERE id='{BillingDatabase.Org}'"));
        Assert.Equal(true, await db.Scalar($"SELECT phone IS NULL FROM organizations WHERE id='{BillingDatabase.Org}'"));
        foreach (var google in new[] { false, true })
        {
            var id = google ? "audit-google" : "audit-email";
            var proof = await OrganizationPhoneHttpTests.Proof(factory, client, google, id);
            Assert.Equal(HttpStatusCode.InternalServerError, (await OrganizationPhoneHttpTests.Register(client, google, id, proof, "0706364866")).StatusCode);
            Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM users"));
            Assert.Equal(2L, await db.Scalar("SELECT count(*) FROM organizations"));
            Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM auth_refresh_tokens"));
            Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM audit_logs"));
            Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM auth_google_onboarding_sessions WHERE completed_at IS NOT NULL"));
            Assert.Equal(0L, await db.Scalar("SELECT count(*) FROM registration_email_challenges WHERE registration_token_used_at IS NOT NULL"));
        }
    }

    [BillingPostgresFact]
    public async Task Unrelated_unique_constraint_is_not_mapped_to_phone_conflict()
    {
        await using var db = await BillingDatabase.Create(false); await OrganizationPhoneHttpTests.Prepare(db);
        await db.Sql("CREATE UNIQUE INDEX organization_test_name_key ON organizations(name)");
        await using (var context = db.Context())
        {
            await context.Database.OpenConnectionAsync(); await context.Database.ExecuteSqlRawAsync("SET ROLE fire3d_api");
            var store = new AuthStore(context); var userId = Guid.NewGuid();
            await using var tx = await store.BeginUserTransactionAsync(userId, default);
            var organization = new Organization { Id = Guid.NewGuid(), Name = "One", Slug = "different-slug", PhoneNumber = "0706364866", Plan = "free", Metadata = "{}", IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            var user = new User { Id = userId, Email = "other-name@example.test", Role = UserRole.OrganizationUser, OrganizationId = organization.Id, IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => store.TryCreateOrganizationWithUserAsync(organization, user, default));
            Assert.Equal("organization_test_name_key", Assert.IsType<PostgresException>(error.InnerException).ConstraintName);
        }
        Assert.Equal(2L, await db.Scalar("SELECT count(*) FROM organizations")); Assert.Equal(3L, await db.Scalar("SELECT count(*) FROM users"));
    }
}
