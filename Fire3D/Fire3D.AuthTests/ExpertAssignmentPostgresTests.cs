using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Fire3D.AuthTests;

// BE#242: thẩm định items are assigned round-robin to one available expert instead of one shared queue; only the assignee decides,
// and PlatformAdmin sees the assignee and reassigns with a reason.
public sealed partial class AuthIntegrationTests
{
    private static async Task<Guid> UserId(HttpClient http) => (await http.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("id").GetGuid();

    private async Task<Guid?> Assignee(Guid review) => (Guid?)await ScalarAsync($"SELECT assignee_id FROM expert_review_assignments WHERE review_id='{review}'");

    // An expert account that exists but cannot receive work: locked, or still at the first-login email step.
    private async Task<Guid> UnavailableExpert(bool locked)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO users(id,organization_id,email,username,full_name,role,is_active,email_verified_at,first_login_step,password_hash,created_at,updated_at)
            SELECT '{id}',NULL,'expert-{id:N}@example.test','u'||left(md5('{id}'),20),'Chuyên viên nghỉ','FireSafetyExpert',{(!locked).ToString().ToLowerInvariant()},
             {(locked ? "now()" : "NULL")},{(locked ? "NULL" : "'VerifyEmail'")},password_hash,now(),now() FROM users WHERE id='{adminId}'
            """);
        return id;
    }

    private static HttpRequestMessage Reassign(Guid review, Guid? expert, string? reason) =>
        new(HttpMethod.Post, $"/api/admin/reviews/{review}/reassign") { Content = JsonContent.Create(new { expertId = expert, reason }) };

    [PostgresFact]
    public async Task Thẩm_định_items_go_round_robin_and_only_the_assignee_decides_until_an_admin_reassigns()
    {
        using var a = await RoleClient("FireSafetyExpert");
        using var b = await RoleClient("FireSafetyExpert");
        var (idA, idB) = (await UserId(a), await UserId(b));
        var locked = await UnavailableExpert(locked: true);
        var unverified = await UnavailableExpert(locked: false);

        // Three submissions in a row go to A, B, A; the locked and the unverified expert get nothing.
        var one = await SubmittedContentReview();
        var two = await SubmittedContentReview();
        var three = await SubmittedContentReview();
        Assert.Equal(new Guid?[] { idA, idB, idA }, [await Assignee(one.Review), await Assignee(two.Review), await Assignee(three.Review)]);
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM expert_review_assignments WHERE assignee_id IN('{locked}','{unverified}')"));

        // B's queue holds B's item by default; assignee=all shows every item with its assignee.
        var mine = await b.GetFromJsonAsync<JsonElement>("/api/expert/reviews?pageSize=100");
        var item = Assert.Single(mine.GetProperty("items").EnumerateArray());
        Assert.Equal((two.Review, idB), (item.GetProperty("reviewId").GetGuid(), item.GetProperty("assigneeId").GetGuid()));
        Assert.Equal(JsonValueKind.String, item.GetProperty("assigneeName").ValueKind);
        var all = await b.GetFromJsonAsync<JsonElement>("/api/expert/reviews?assignee=all&pageSize=100");
        Assert.Equal(3, all.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await b.GetAsync("/api/expert/reviews?assignee=someone")).StatusCode);

        // B reads A's item but cannot decide it: its own code, not 412; the item stays waiting.
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync($"/api/expert/reviews/{one.Review}")).StatusCode);
        var refused = await b.SendAsync(ExpertDecision(one.Review, "approve", "\"1\"", "b-on-a"));
        Assert.Equal((HttpStatusCode.Forbidden, "REVIEW_NOT_ASSIGNED_TO_YOU"), (refused.StatusCode, await ExpertCode(refused)));
        Assert.Equal("Submitted", (string?)await ScalarAsync($"SELECT status FROM scenario_content_reviews WHERE id='{one.Review}'"));

        // PlatformAdmin sees who holds each item and moves one from A to B with a reason (audited).
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var board = await client.GetFromJsonAsync<JsonElement>($"/api/admin/reviews?assigneeId={idA}&pageSize=100");
        Assert.Equal(2, board.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await b.GetAsync("/api/admin/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Reassign(one.Review, idB, " "))).StatusCode);
        var toLocked = await client.SendAsync(Reassign(one.Review, locked, "Chuyên viên A nghỉ phép"));
        Assert.Equal(((HttpStatusCode)422, "EXPERT_NOT_AVAILABLE"), (toLocked.StatusCode, await ExpertCode(toLocked)));
        var moved = await client.SendAsync(Reassign(one.Review, idB, "Chuyên viên A nghỉ phép"));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(idB, (await moved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("assigneeId").GetGuid());
        Assert.Equal(1L, await ScalarAsync($"""
            SELECT count(*) FROM audit_logs WHERE target_entity='ExpertReviewAssignment' AND target_id='{one.Review}'
             AND old_values->>'assigneeId'='{idA}' AND new_values->>'assigneeId'='{idB}' AND new_values->>'reason'='Chuyên viên A nghỉ phép'
            """));
        // Reassigning to the current assignee changes nothing.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Reassign(one.Review, idB, "Lặp lại"))).StatusCode);
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_entity='ExpertReviewAssignment' AND target_id='{one.Review}'"));

        // Now B decides it and A cannot.
        var byA = await a.SendAsync(ExpertDecision(one.Review, "approve", "\"1\"", "a-after-move"));
        Assert.Equal((HttpStatusCode.Forbidden, "REVIEW_NOT_ASSIGNED_TO_YOU"), (byA.StatusCode, await ExpertCode(byA)));
        Assert.Equal(HttpStatusCode.OK, (await b.SendAsync(ExpertDecision(one.Review, "approve", "\"1\"", "b-after-move"))).StatusCode);
        // A decided item is no longer reassigned.
        var decided = await client.SendAsync(Reassign(one.Review, idA, "Trả lại"));
        Assert.Equal((HttpStatusCode.Conflict, "REVIEW_NOT_PENDING"), (decided.StatusCode, await ExpertCode(decided)));
        client.DefaultRequestHeaders.Authorization = null;
    }

    [PostgresFact]
    public async Task An_item_submitted_while_no_expert_is_available_is_assigned_when_one_is()
    {
        var waiting = await SubmittedContentReview();
        Assert.Null(await Assignee(waiting.Review));
        using var expert = await RoleClient("FireSafetyExpert");
        var queue = await expert.GetFromJsonAsync<JsonElement>("/api/expert/reviews?pageSize=100");
        Assert.Equal(waiting.Review, Assert.Single(queue.GetProperty("items").EnumerateArray()).GetProperty("reviewId").GetGuid());
        Assert.Equal(await UserId(expert), await Assignee(waiting.Review));
        Assert.Equal(HttpStatusCode.OK, (await expert.SendAsync(ExpertDecision(waiting.Review, "reject", "\"1\"", "late-expert", new { reason = "Thiếu điểm tập kết." }))).StatusCode);
    }
}
