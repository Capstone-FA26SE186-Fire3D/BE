using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Fire3D.Application.Content;
using Fire3D.Infrastructure.Content;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // Issue #83: the typed OpenAPI records must describe every field the Learn gates return, no more and no less.
    [PostgresFact]
    public async Task Learn_responses_match_their_declared_openapi_records_exactly()
    {
        var admin = await AdminFamily(); var trainee = await SeedTrainee(); var source = await SeedKnowledgeSource("Approved");
        var strict = new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        await WithContentRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new ContentGates(db);
            async Task<JsonElement> Ok(Task<Fire3D.Application.Authentication.AuthResult<JsonElement>> call)
            {
                var result = await call; Assert.True(result.IsSuccess, result.Error?.Code); return result.Value;
            }
            var situation = await Ok(gates.LearnAdminAsync("CreateSituation", adminId, admin, Guid.Empty, new CreateLearnSituationRequest("shape-fire", "Cháy"), "shape-situation", null, default));
            situation.Deserialize<LearnSituationView>(strict);
            var content = LearnContent.Normalize(new LearnVersionInput("Video", "Video thoát hiểm", "Tóm tắt", null,
                new JsonArray(JsonNode.Parse("""{"type":"video","url":"https://youtu.be/dQw4w9WgXcQ"}""")), [situation.GetProperty("id").GetGuid()], [new(source, "Primary")])).Content!;
            var post = await Ok(gates.LearnAdminAsync("CreatePost", adminId, admin, Guid.Empty, new { slug = "shape-post", version = content, publish = true }, "shape-post", null, default));
            post.Deserialize<LearnPostView>(strict);
            post.GetProperty("publishedVersion").Deserialize<LearnVersionView>(strict);
            var postId = post.GetProperty("id").GetGuid();
            (await Ok(gates.LearnAdminAsync("CreateVersion", adminId, admin, postId, content, "shape-version", null, default))).Deserialize<LearnVersionView>(strict);
            (await Ok(gates.LearnAdminAsync("List", adminId, admin, Guid.Empty, new { }, null, null, default))).Deserialize<LearnPostAdminPage>(strict);
            (await Ok(gates.LearnAdminAsync("Get", adminId, admin, postId, new { }, null, null, default))).Deserialize<LearnPostView>(strict);
            await using (var owner = BuildingContext(testConnection))
                (await Ok(new ContentGates(owner).CatalogAsync("Situations", adminId, admin, new { page = 1, pageSize = 20 }, default))).Deserialize<LearnSituationPage>(strict);
            (await Ok(gates.LearnPublicAsync("Situations", null, null, new { }, default))).Deserialize<List<LearnSituationView>>(strict);
            (await Ok(gates.LearnPublicAsync("Posts", null, null, new { }, default))).Deserialize<LearnPostPage>(strict);
            (await Ok(gates.LearnPublicAsync("Post", null, null, new { slug = "shape-post" }, default))).Deserialize<LearnPostView>(strict);
            (await Ok(gates.LearnPublicAsync("Bookmark", trainee.Id, trainee.Family, new { postId }, default))).Deserialize<LearnBookmarkState>(strict);
            (await Ok(gates.LearnPublicAsync("BookmarkState", trainee.Id, trainee.Family, new { postId }, default))).Deserialize<LearnBookmarkState>(strict);
            (await Ok(gates.LearnPublicAsync("Bookmarks", trainee.Id, trainee.Family, new { }, default))).Deserialize<LearnBookmarkPage>(strict);
            var hidden = await Ok(gates.LearnAdminAsync("Hide", adminId, admin, postId, new { }, null, post.GetProperty("revision").GetInt64(), default));
            hidden.Deserialize<LearnPostView>(strict);
            (await Ok(gates.LearnPublicAsync("Bookmarks", trainee.Id, trainee.Family, new { }, default))).Deserialize<LearnBookmarkPage>(strict);
            (await Ok(gates.LearnAdminAsync("UpdateSituation", adminId, admin, situation.GetProperty("id").GetGuid(), new Dictionary<string, object?> { ["name"] = "Cháy lớn" }, null, null, default)))
                .Deserialize<LearnSituationAdminView>(strict);
        });
    }
}
