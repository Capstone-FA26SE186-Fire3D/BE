using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Application.Billing;
using Fire3D.Infrastructure.Billing;
using Fire3D.Infrastructure.Scenarios;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // A Submitted content review of a fresh ready package; returns its id and the submitter (the package owner, family = owner id).
    private async Task<(Guid Review, Guid Owner, Guid Version)> SubmittedContentReview()
    {
        var f = await SeedReadyPackage();
        await using var db = BuildingContext(testConnection);
        var submitted = await new ScenarioReadinessStore(db).ExecuteAsync("Submit", f.Owner, f.Owner, f.Version, null, new { }, "submit-" + f.Version, default);
        Assert.True(submitted.IsSuccess, submitted.Error?.Code);
        return (submitted.Value.GetProperty("reviewId").GetGuid(), f.Owner, f.Version);
    }

    private static HttpRequestMessage ExpertDecision(Guid review, string action, string? ifMatch, string? key, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/expert/reviews/{review}/{action}") { Content = JsonContent.Create(body ?? new { }) };
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static async Task<string?> ExpertCode(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("code", out var code) ? code.GetString() : null;

    // BE#211: PlatformAdmin creates expert accounts; an expert has no organization and cannot sign up by itself.
    [PostgresFact]
    public async Task Platform_admin_creates_fire_safety_expert_accounts_without_an_organization()
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var email = $"expert-{Guid.NewGuid():N}@example.test";
        var created = await client.PostAsJsonAsync("/api/accounts", new { email, password, fullName = "Chuyên viên PCCC", role = "FireSafetyExpert" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("FireSafetyExpert", (string?)await ScalarAsync($"SELECT role::text FROM users WHERE email='{email}'"));
        Assert.Equal(DBNull.Value, await ScalarAsync($"SELECT organization_id FROM users WHERE email='{email}'"));
        var org = Guid.NewGuid();
        var withOrg = await client.PostAsJsonAsync("/api/accounts", new { email = $"expert-{Guid.NewGuid():N}@example.test", password, role = "FireSafetyExpert", organizationId = org });
        Assert.Equal(HttpStatusCode.BadRequest, withOrg.StatusCode);
        // The expert signs in normally.
        Assert.False(string.IsNullOrEmpty((await LoginAsync(email)).AccessToken));
    }

    // BE#211 acceptance: shared queue, If-Match / Idempotency-Key decisions, reason on reject, 403 for other roles and for billing,
    // decided items readable by every expert, the submitter notified.
    [PostgresFact]
    public async Task Fire_safety_experts_share_one_queue_and_decide_once_with_if_match_and_idempotency()
    {
        var approved = await SubmittedContentReview();
        var rejected = await SubmittedContentReview();
        using var first = await RoleClient("FireSafetyExpert");
        using var second = await RoleClient("FireSafetyExpert");
        var org = (Guid)(await ScalarAsync($"SELECT organization_id FROM users WHERE id='{approved.Owner}'"))!;
        using var organization = await RoleClient("OrganizationUser", org);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);

        // Queue: both waiting items, oldest first, revision 1. No Infrastructure item until BE#213.
        var queue = await first.GetFromJsonAsync<JsonElement>("/api/expert/reviews?kind=Scenario&pageSize=100");
        var ids = queue.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("reviewId").GetGuid()).ToList();
        Assert.True(ids.IndexOf(approved.Review) >= 0 && ids.IndexOf(approved.Review) < ids.IndexOf(rejected.Review));
        var item = queue.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("reviewId").GetGuid() == approved.Review);
        Assert.Equal(("Scenario", "Submitted", 1L), (item.GetProperty("kind").GetString(), item.GetProperty("status").GetString(), item.GetProperty("revision").GetInt64()));
        Assert.Equal(0, (await first.GetFromJsonAsync<JsonElement>("/api/expert/reviews?kind=Infrastructure")).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await first.GetAsync("/api/expert/reviews?status=Draft")).StatusCode);
        var detail = await first.GetAsync($"/api/expert/reviews/{approved.Review}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal("\"1\"", detail.Headers.ETag!.Tag);
        Assert.Equal(JsonValueKind.Object, (await detail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("content").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await first.GetAsync($"/api/expert/reviews/{Guid.NewGuid()}")).StatusCode);

        // Experts get no billing; OrganizationUser and PlatformAdmin get no thẩm định.
        Assert.Equal(HttpStatusCode.Forbidden, (await first.GetAsync("/api/billing/service-packages")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await first.GetAsync("/api/organizations/me/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await organization.SendAsync(ExpertDecision(approved.Review, "approve", "\"1\"", "org"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(ExpertDecision(approved.Review, "approve", "\"1\"", "admin"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await organization.GetAsync("/api/expert/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/expert/reviews")).StatusCode);

        // Preconditions and reason.
        var noReason = await first.SendAsync(ExpertDecision(rejected.Review, "reject", "\"1\"", "no-reason", new { reason = " " }));
        Assert.Equal((HttpStatusCode.BadRequest, "VALIDATION_ERROR"), (noReason.StatusCode, await ExpertCode(noReason)));
        Assert.Equal(HttpStatusCode.BadRequest, (await first.SendAsync(ExpertDecision(rejected.Review, "reject", "\"1\"", "long", new { reason = new string('x', 2001) }))).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await first.SendAsync(ExpertDecision(approved.Review, "approve", null, "no-if-match"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await first.SendAsync(ExpertDecision(approved.Review, "approve", "1", "bad-if-match"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await first.SendAsync(ExpertDecision(approved.Review, "approve", "\"1\"", null))).StatusCode);

        // First expert decides; the same key replays one decision; the second expert's stale If-Match is 412.
        var decided = await first.SendAsync(ExpertDecision(approved.Review, "approve", "\"1\"", "approve-1"));
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        Assert.Equal("\"2\"", decided.Headers.ETag!.Tag);
        var body = await decided.Content.ReadFromJsonAsync<JsonElement>();
        var firstExpert = body.GetProperty("decidedBy").GetGuid();
        Assert.Equal(("Approved", 2L), (body.GetProperty("status").GetString(), body.GetProperty("revision").GetInt64()));
        var replay = await first.SendAsync(ExpertDecision(approved.Review, "approve", "\"1\"", "approve-1"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(firstExpert, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("decidedBy").GetGuid());
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_id='{approved.Version}' AND new_values->>'decidedByRole'='FireSafetyExpert'"));
        var late = await second.SendAsync(ExpertDecision(approved.Review, "reject", "\"1\"", "late", new { reason = "Thiếu lối thoát." }));
        Assert.Equal((HttpStatusCode.PreconditionFailed, "PRECONDITION_FAILED"), (late.StatusCode, await ExpertCode(late)));
        Assert.Equal(HttpStatusCode.Conflict, (await second.SendAsync(ExpertDecision(approved.Review, "reject", "\"2\"", "decided", new { reason = "Thiếu lối thoát." }))).StatusCode);
        Assert.Equal("Approved", (string?)await ScalarAsync($"SELECT status FROM scenario_content_reviews WHERE id='{approved.Review}'"));

        var rejection = await second.SendAsync(ExpertDecision(rejected.Review, "reject", "\"1\"", "reject-1", new { reason = "Thiếu điểm tập kết." }));
        Assert.Equal(HttpStatusCode.OK, rejection.StatusCode);
        Assert.Equal("Thiếu điểm tập kết.", (await rejection.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reason").GetString());

        // Decided items stay readable by every expert (read-only), newest decision first.
        var decidedQueue = await second.GetFromJsonAsync<JsonElement>("/api/expert/reviews?status=Approved&pageSize=100");
        var seen = decidedQueue.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("reviewId").GetGuid() == approved.Review);
        Assert.Equal(firstExpert, seen.GetProperty("decidedBy").GetGuid());
        Assert.Contains(rejected.Review, (await first.GetFromJsonAsync<JsonElement>("/api/expert/reviews?status=Rejected&pageSize=100"))
            .GetProperty("items").EnumerateArray().Select(x => x.GetProperty("reviewId").GetGuid()));

        // The submitter sees both results as web notifications; the organization status reads the same decision.
        await using var db = BuildingContext(testConnection);
        var notes = await new OrganizationNotifications(db).List(approved.Owner, approved.Owner, new NotificationQuery(), default);
        var note = Assert.Single(notes.Items, x => x.ContentReviewId == approved.Review);
        Assert.Equal(("ContentReviewApproved", approved.Version, true), (note.Type, note.ScenarioVersionId, note.StillRelevant));
        Assert.Null(note.EntitlementId);
        var rejectedNotes = await new OrganizationNotifications(db).List(rejected.Owner, rejected.Owner, new NotificationQuery(Type: "ContentReviewRejected"), default);
        Assert.Contains("Thiếu điểm tập kết.", Assert.Single(rejectedNotes.Items).Body);
        Assert.DoesNotContain("phê duyệt", note.Body + note.Title, StringComparison.OrdinalIgnoreCase);
    }
}
