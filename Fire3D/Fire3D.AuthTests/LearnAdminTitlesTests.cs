using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Fire3D.Application.Content;
using Fire3D.Infrastructure.Content;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // BE#154: every admin list row is named by its latest version, whatever the post state; the public title stays publishedVersion.title.
    [PostgresFact]
    public async Task Learn_admin_list_names_every_post_by_its_latest_version()
    {
        var admin = await AdminFamily();
        var strict = new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        await WithContentRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new ContentGates(db);
            async Task<JsonElement> Ok(Task<Fire3D.Application.Authentication.AuthResult<JsonElement>> call)
            {
                var result = await call; Assert.True(result.IsSuccess, result.Error?.Code); return result.Value;
            }
            JsonElement Content(string kind, string title) => JsonSerializer.SerializeToElement(LearnContent.Normalize(new LearnVersionInput(kind, title, "Tóm tắt " + title, null,
                new JsonArray(JsonNode.Parse("""{"type":"paragraph","text":"Stay low."}""")))).Content!);
            Task<JsonElement> Create(string slug, string kind, string title, bool publish) =>
                Ok(gates.LearnAdminAsync("CreatePost", adminId, admin, Guid.Empty, new { slug, version = Content(kind, title), publish }, slug, null, default));
            async Task<LearnPostAdminPage> List(object input) => (await Ok(gates.LearnAdminAsync("List", adminId, admin, Guid.Empty, input, null, null, default))).Deserialize<LearnPostAdminPage>(strict)!;

            var unpublished = await Create("title-unpublished", "Tip", "Bản nháp đầu", false);
            var revised = await Create("title-revised", "Article", "Tiêu đề công khai", true);
            var published = await Create("title-published", "Article", "Bài công khai", true);
            var hidden = await Create("title-hidden", "Tip", "Bài đã ẩn", true);
            var draft = await Ok(gates.LearnAdminAsync("CreateVersion", adminId, admin, revised.GetProperty("id").GetGuid(), Content("Tip", "Tiêu đề bản nháp"), "title-revised-v2", null, default));
            Assert.True((await gates.LearnAdminAsync("Hide", adminId, admin, hidden.GetProperty("id").GetGuid(), new { }, null, hidden.GetProperty("revision").GetInt64(), default)).IsSuccess);
            var deleted = await Create("title-deleted", "Tip", "Bài nháp đã xoá", false);
            Assert.True((await gates.LearnAdminAsync("Delete", adminId, admin, deleted.GetProperty("id").GetGuid(), new { }, null, deleted.GetProperty("revision").GetInt64(), default)).IsSuccess);

            var page = await List(new { });
            Assert.Equal(5, page.Total);
            Assert.Equal(["title-deleted", "title-hidden", "title-revised", "title-published", "title-unpublished"], page.Items.Select(x => x.Slug));
            Assert.Equal(page.Items.OrderByDescending(x => x.UpdatedAt).ThenBy(x => x.Id.ToString()).Select(x => x.Id), page.Items.Select(x => x.Id));
            var rows = page.Items.ToDictionary(x => x.Slug);
            Guid FirstVersion(JsonElement post) => post.GetProperty("versions")[0].GetProperty("id").GetGuid();

            // Never published: the Draft names the row, and there is no public version.
            var row = rows["title-unpublished"];
            Assert.Equal(("Unpublished", "Bản nháp đầu", "Tip", FirstVersion(unpublished), 1, "Draft", FirstVersion(unpublished), 1), (row.PublicationStatus, row.Title, row.Kind, row.LatestVersionId, row.LatestVersionNumber, row.LatestVersionStatus, row.DraftVersionId, row.VersionCount));
            Assert.Null(row.PublishedVersion);

            // Published with a newer Draft: the row shows the Draft while the public title is unchanged.
            row = rows["title-revised"];
            var draftId = draft.GetProperty("id").GetGuid();
            Assert.Equal(("Published", "Tiêu đề bản nháp", "Tip", draftId, 2, "Draft", draftId, 2), (row.PublicationStatus, row.Title, row.Kind, row.LatestVersionId, row.LatestVersionNumber, row.LatestVersionStatus, row.DraftVersionId, row.VersionCount));
            Assert.Equal(("Tiêu đề công khai", "Article"), (row.PublishedVersion!.Title, row.PublishedVersion.Kind));

            // Published without a Draft: the latest version is the public one.
            row = rows["title-published"];
            Assert.Equal(("Bài công khai", "Article", published.GetProperty("publishedVersionId").GetGuid(), "Published", "Bài công khai"), (row.Title, row.Kind, row.LatestVersionId, row.LatestVersionStatus, row.PublishedVersion!.Title));
            Assert.Null(row.DraftVersionId);

            // Hidden and Deleted posts keep their titles.
            row = rows["title-hidden"];
            Assert.Equal(("Hidden", "Bài đã ẩn", "Tip", "Published"), (row.PublicationStatus, row.Title, row.Kind, row.LatestVersionStatus));
            row = rows["title-deleted"];
            Assert.Equal(("Deleted", "Bài nháp đã xoá", "Tip", FirstVersion(deleted), "Draft"), (row.PublicationStatus, row.Title, row.Kind, row.LatestVersionId, row.LatestVersionStatus));
            Assert.Null(row.PublishedVersion);

            // Status filter and paging are unchanged: pages of two concatenate to the full order.
            var paged = new List<string>();
            for (var n = 1; n <= 3; n++)
            {
                var part = await List(new { page = n, pageSize = 2 });
                Assert.Equal((5, n, 2), (part.Total, part.Page, part.PageSize));
                paged.AddRange(part.Items.Select(x => x.Slug));
            }
            Assert.Equal(page.Items.Select(x => x.Slug), paged);
            var deletedOnly = await List(new { status = "Deleted" });
            Assert.Equal((1, "Bài nháp đã xoá"), (deletedOnly.Total, deletedOnly.Items.Single().Title));
        });
    }
}
