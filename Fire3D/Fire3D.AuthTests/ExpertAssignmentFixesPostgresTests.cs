using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fire3D.Infrastructure.Scenarios;
using Xunit;

namespace Fire3D.AuthTests;

// BE#245: experts created before BE#235 take work; PlatformAdmin no longer decides thẩm định; an unavailable expert gives work back.
public sealed partial class AuthIntegrationTests
{
    // The thẩm định decision a fixture needs, made the only way left (BE#245): by the expert assigned to the version's waiting review.
    private async Task DecideAsExpert(Guid version, bool approve, string? reason = null)
    {
        var expert = await SharedExpert();
        var review = (Guid)(await ScalarAsync($"SELECT id FROM scenario_content_reviews WHERE scenario_version_id='{version}' AND status='Submitted' ORDER BY submitted_at DESC LIMIT 1"))!;
        var response = await expert.SendAsync(ExpertDecision(review, approve ? "approve" : "reject", "\"1\"", "expert-" + review, approve ? null : new { reason }));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task An_expert_created_before_first_sign_in_verification_is_given_work_and_decides_it()
    {
        // Created by PlatformAdmin before BE#235: no email_verified_at and no first_login_step.
        using var legacy = await RoleClient("FireSafetyExpert");
        var id = await UserId(legacy);
        await ExecuteAsync($"UPDATE users SET email_verified_at=NULL,first_login_step=NULL WHERE id='{id}'");
        var item = await SubmittedContentReview();
        Assert.Equal(id, await Assignee(item.Review));
        Assert.Equal(HttpStatusCode.OK, (await legacy.SendAsync(ExpertDecision(item.Review, "approve", "\"1\"", "legacy-approve"))).StatusCode);
        // Still not given work while at the first-login email step.
        await ExecuteAsync($"UPDATE users SET first_login_step='VerifyEmail' WHERE id='{id}'");
        var next = await SubmittedContentReview();
        Assert.Null(await Assignee(next.Review));
    }

    [PostgresFact]
    public async Task Platform_admin_can_no_longer_approve_or_reject_scenario_content()
    {
        var item = await SubmittedContentReview();
        var hashes = await ScalarAsync($"SELECT content_hash||':'||rubric_hash FROM scenario_content_reviews WHERE id='{item.Review}'") as string;
        var (content, rubric) = (hashes!.Split(':')[0], hashes.Split(':')[1]);
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        foreach (var action in new[] { "approve", "reject" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/scenario-versions/{item.Version}/{action}")
                { Content = JsonContent.Create(new { contentHash = content, rubricHash = rubric, reason = "Thiếu lối thoát." }) };
            request.Headers.Add("Idempotency-Key", "admin-" + action);
            var response = await client.SendAsync(request);
            Assert.Equal(((HttpStatusCode)410, "ADMIN_REVIEW_REMOVED"), (response.StatusCode, await ExpertCode(response)));
        }
        client.DefaultRequestHeaders.Authorization = null;
        // The gate refuses it too, whoever calls it.
        await using var db = BuildingContext(testConnection);
        var direct = await new ScenarioReadinessStore(db).ExecuteAsync("Approve", adminId, adminId, item.Version, null,
            new { contentHash = content, rubricHash = rubric }, "admin-direct", default);
        Assert.Equal(("ADMIN_REVIEW_REMOVED", 410), (direct.Error?.Code, direct.Error?.Status));
        Assert.Equal("Submitted", (string?)await ScalarAsync($"SELECT status FROM scenario_content_reviews WHERE id='{item.Review}'"));
    }

    [PostgresFact]
    public async Task A_locked_expert_gives_back_waiting_items_and_keeps_decided_ones()
    {
        using var a = await RoleClient("FireSafetyExpert");
        using var b = await RoleClient("FireSafetyExpert");
        var (idA, idB) = (await UserId(a), await UserId(b));
        var decided = await SubmittedContentReview();
        var middle = await SubmittedContentReview();
        var waiting = await SubmittedContentReview();
        Assert.Equal(new Guid?[] { idA, idB, idA }, [await Assignee(decided.Review), await Assignee(middle.Review), await Assignee(waiting.Review)]);
        Assert.Equal(HttpStatusCode.OK, (await a.SendAsync(ExpertDecision(decided.Review, "approve", "\"1\"", "a-decides"))).StatusCode);

        await ExecuteAsync($"UPDATE users SET is_active=false WHERE id='{idA}'");
        Assert.Equal(idB, await Assignee(waiting.Review));
        Assert.Equal(idA, await Assignee(decided.Review));
        Assert.Equal(HttpStatusCode.OK, (await b.SendAsync(ExpertDecision(waiting.Review, "reject", "\"1\"", "b-takes-over", new { reason = "Thiếu điểm tập kết." }))).StatusCode);
    }
}
