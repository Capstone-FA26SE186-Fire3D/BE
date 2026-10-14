using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Content;
using Fire3D.Application.Learning;
using Fire3D.Infrastructure.Content;
using Fire3D.Infrastructure.Learning;
using Microsoft.Extensions.Options;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // Issue #86 (Learn part) and the 14/10 Docs review: admin list rows, Show/RAG source rules, situation edits, bookmark state.
    [PostgresFact]
    public async Task Learn_admin_list_show_rules_situation_updates_and_bookmark_state()
    {
        var admin = await AdminFamily(); var trainee = await SeedTrainee(); var source = await SeedKnowledgeSource("Approved");
        await WithContentRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new ContentGates(db);
            var situation = (await gates.LearnAdminAsync("CreateSituation", adminId, admin, Guid.Empty, new CreateLearnSituationRequest("smoke-room", "Khói"), "gap-situation", null, default)).Value.GetProperty("id").GetGuid();
            JsonElement Content(string title) => JsonSerializer.SerializeToElement(LearnContent.Normalize(new LearnVersionInput("Tip", title, "Tóm tắt " + title, null,
                new JsonArray(JsonNode.Parse("""{"type":"paragraph","text":"Stay low."}""")), [situation], [new(source, "Primary")])).Content!);
            var published = (await gates.LearnAdminAsync("CreatePost", adminId, admin, Guid.Empty, new { slug = "gap-published", version = Content("Bài đã đăng"), publish = true }, "gap-published", null, default)).Value;
            await gates.LearnAdminAsync("CreatePost", adminId, admin, Guid.Empty, new { slug = "gap-draft", version = Content("Bài nháp"), publish = false }, "gap-draft", null, default);

            // Admin list rows carry the latest title/kind even for a never-published post, and its open Draft.
            var rows = (await gates.LearnAdminAsync("List", adminId, admin, Guid.Empty, new { }, null, null, default)).Value.GetProperty("items").EnumerateArray()
                .ToDictionary(x => x.GetProperty("slug").GetString()!);
            Assert.Equal(("Bài nháp", "Tip", 1), (rows["gap-draft"].GetProperty("title").GetString(), rows["gap-draft"].GetProperty("kind").GetString(), rows["gap-draft"].GetProperty("draftVersionNumber").GetInt32()));
            Assert.Equal(JsonValueKind.Null, rows["gap-published"].GetProperty("draftVersionId").ValueKind);
            Assert.Equal("Bài đã đăng", rows["gap-published"].GetProperty("title").GetString());

            // Bookmark state of one post for the signed-in Trainee.
            var post = published.GetProperty("id").GetGuid();
            Assert.False((await gates.LearnPublicAsync("BookmarkState", trainee.Id, trainee.Family, new { postId = post }, default)).Value.GetProperty("bookmarked").GetBoolean());
            Assert.True((await gates.LearnPublicAsync("Bookmark", trainee.Id, trainee.Family, new { postId = post }, default)).IsSuccess);
            Assert.True((await gates.LearnPublicAsync("BookmarkState", trainee.Id, trainee.Family, new { postId = post }, default)).Value.GetProperty("bookmarked").GetBoolean());
            Assert.Equal("FORBIDDEN", (await gates.LearnPublicAsync("BookmarkState", adminId, admin, new { postId = post }, default)).Error?.Code);

            // A source retired while the post is hidden blocks Show and removes the post from retrieval.
            var version = published.GetProperty("publishedVersionId").GetGuid();
            var hidden = await gates.LearnAdminAsync("Hide", adminId, admin, post, new { }, null, published.GetProperty("revision").GetInt64(), default);
            Assert.True(hidden.IsSuccess, hidden.Error?.Code);
            Assert.True((bool)(await ScalarAsync($"SELECT learn_rag_eligible('{post}','{version}')"))!);
            await ExecuteAsync($"UPDATE knowledge_sources SET approval_status='Retired' WHERE id='{source}'");
            Assert.False((bool)(await ScalarAsync($"SELECT learn_rag_eligible('{post}','{version}')"))!);
            var revision = hidden.Value.GetProperty("revision").GetInt64();
            Assert.Equal("LEARN_SOURCE_NOT_APPROVED", (await gates.LearnAdminAsync("Show", adminId, admin, post, new { }, null, revision, default)).Error?.Code);
            await ExecuteAsync($"UPDATE knowledge_sources SET approval_status='Approved' WHERE id='{source}'");

            // Situations can be renamed and deactivated, never deleted; an inactive one blocks Show and leaves public filters.
            var renamed = await gates.LearnAdminAsync("UpdateSituation", adminId, admin, situation, new Dictionary<string, object?> { ["name"] = "Khói dày" }, null, null, default);
            Assert.Equal(("Khói dày", true), (renamed.Value.GetProperty("name").GetString(), renamed.Value.GetProperty("isActive").GetBoolean()));
            Assert.True((await gates.LearnAdminAsync("UpdateSituation", adminId, admin, situation, new Dictionary<string, object?> { ["isActive"] = false }, null, null, default)).IsSuccess);
            Assert.Equal("LEARN_SITUATION_INVALID", (await gates.LearnAdminAsync("Show", adminId, admin, post, new { }, null, revision, default)).Error?.Code);
            Assert.True((await gates.LearnAdminAsync("UpdateSituation", adminId, admin, situation, new Dictionary<string, object?> { ["isActive"] = true }, null, null, default)).IsSuccess);
            Assert.True((await gates.LearnAdminAsync("Show", adminId, admin, post, new { }, null, revision, default)).IsSuccess);
            Assert.Equal("VALIDATION_ERROR", (await gates.LearnAdminAsync("UpdateSituation", adminId, admin, situation, new Dictionary<string, object?>(), null, null, default)).Error?.Code);
            Assert.Equal("NOT_FOUND", (await gates.LearnAdminAsync("UpdateSituation", adminId, admin, Guid.NewGuid(), new Dictionary<string, object?> { ["name"] = "x" }, null, null, default)).Error?.Code);
            Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM audit_logs WHERE target_entity='LearnSituation' AND target_id='{situation}' AND new_values->>'operation'='UpdateSituation' AND new_values->>'name'='Khói dày'"));
        });
    }

    // Issue #86 (review and analytics part): reviewers and analytics rows carry display names, not only IDs.
    [PostgresFact]
    public async Task Review_summary_and_admin_training_analytics_carry_names()
    {
        var f = await PublishedTraining(ScorableRubric);
        var trainee = await SeedTrainee();
        await WithLearnerRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            await Running(new LearnerSessions(db, Options.Create(LearnerOptions)), new LearnerCaller(trainee.Id, trainee.Family, null), f.Training, "Learn", "names");
        });
        using var review = JsonDocument.Parse((string)(await ScalarAsync("SELECT scenario_review_summary(id)::text FROM scenario_content_reviews ORDER BY submitted_at LIMIT 1"))!);
        Assert.False(string.IsNullOrEmpty(review.RootElement.GetProperty("submittedByName").GetString()));
        Assert.False(string.IsNullOrEmpty(review.RootElement.GetProperty("decidedByName").GetString()));
        using var analytics = JsonDocument.Parse((string)(await ScalarAsync("SELECT analytics_training(NULL,now()-interval '1 day',now()+interval '1 day',now(),300)::text"))!);
        var training = analytics.RootElement.GetProperty("byTraining")[0];
        Assert.Equal(f.Training, training.GetProperty("trainingId").GetGuid());
        Assert.Equal(await ScalarAsync($"SELECT name FROM trainings WHERE id='{f.Training}'"), training.GetProperty("trainingName").GetString());
        Assert.False(string.IsNullOrEmpty(analytics.RootElement.GetProperty("byOrganization")[0].GetProperty("organizationName").GetString()));
    }
}
