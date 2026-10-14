using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Application.Content;
using Fire3D.Infrastructure.Content;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // Issue #79: anonymous Learn list with diacritic-insensitive search, multi-value situation/kind filters and exact totals.
    [PostgresFact]
    public async Task Learn_public_list_searches_without_diacritics_and_filters_before_paging()
    {
        var admin = await AdminFamily(); var source = await SeedKnowledgeSource("Approved");
        await WithContentRuntime(async runtime =>
        {
            await using var db = BuildingContext(runtime);
            var gates = new ContentGates(db);
            async Task<Guid> Situation(string slug, string name) =>
                (await gates.LearnAdminAsync("CreateSituation", adminId, admin, Guid.Empty, new CreateLearnSituationRequest(slug, name), slug, null, default)).Value.GetProperty("id").GetGuid();
            var home = await Situation("fire-at-home", "Cháy tại nhà"); var smoke = await Situation("smoke", "Khói");
            async Task<JsonElement> Post(string slug, string kind, string title, Guid[] situations, bool publish)
            {
                var block = kind == "Video" ? """{"type":"video","url":"https://youtu.be/dQw4w9WgXcQ"}""" : """{"type":"paragraph","text":"Use the nearest safe exit."}""";
                var input = new LearnVersionInput(kind, title, "Tóm tắt " + title, null, new JsonArray(JsonNode.Parse(block)), situations, [new(source, "Primary")]);
                var created = await gates.LearnAdminAsync("CreatePost", adminId, admin, Guid.Empty, new { slug, version = LearnContent.Normalize(input).Content!, publish }, slug, null, default);
                Assert.True(created.IsSuccess, created.Error?.Code);
                return created.Value;
            }
            await Post("thoat-hiem", "Article", "Thoát hiểm khi CHÁY", [home], true);
            await Post("duong-thoat-khoi", "Tip", "Đường thoát khói", [smoke], true);
            await Post("binh-chua-chay", "Video", "Video bình chữa cháy", [], true);
            await Post("chay-ban-nhap", "Article", "Cháy bản nháp", [home], false);
            var hidden = await Post("chay-bi-an", "Article", "Cháy bị ẩn", [home], true);
            Assert.True((await gates.LearnAdminAsync("Hide", adminId, admin, hidden.GetProperty("id").GetGuid(), new { }, null, hidden.GetProperty("revision").GetInt64(), default)).IsSuccess);
        });

        var anonymous = factory!.CreateClient();
        async Task<(int Total, string Slugs)> List(string query)
        {
            var response = await anonymous.GetAsync("/api/learn/posts?" + query);
            Assert.True(response.StatusCode == HttpStatusCode.OK, query + " -> " + await response.Content.ReadAsStringAsync());
            var page = await response.Content.ReadFromJsonAsync<JsonElement>();
            return (page.GetProperty("total").GetInt32(), string.Join(",", page.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("slug").GetString()!).Order()));
        }
        Assert.Equal((3, "binh-chua-chay,duong-thoat-khoi,thoat-hiem"), await List(""));
        // Case and Vietnamese diacritics are folded on both sides; drafts and hidden posts never match or count.
        Assert.Equal((2, "binh-chua-chay,thoat-hiem"), await List("search=chay"));
        Assert.Equal((1, "duong-thoat-khoi"), await List("search=DUONG%20THOAT"));
        Assert.Equal((1, "duong-thoat-khoi"), await List("search=" + Uri.EscapeDataString("đường")));
        Assert.Equal((1, "thoat-hiem"), await List("search=" + Uri.EscapeDataString("tóm tắt thoát")));
        Assert.Equal((0, ""), await List("search=50%25"));
        // Repeated situation and kind values are OR; different filters are AND; the legacy single situation still works.
        Assert.Equal((2, "duong-thoat-khoi,thoat-hiem"), await List("situation=fire-at-home&situation=smoke"));
        Assert.Equal((1, "thoat-hiem"), await List("situation=fire-at-home"));
        Assert.Equal((2, "binh-chua-chay,duong-thoat-khoi"), await List("kind=tip&kind=Video"));
        Assert.Equal((1, "binh-chua-chay"), await List("search=chay&kind=Video"));
        Assert.Equal((0, ""), await List("search=chay&situation=smoke"));
        // Filters run before paging: the total stays exact while a page holds one item.
        var paged = await anonymous.GetFromJsonAsync<JsonElement>("/api/learn/posts?search=chay&pageSize=1&page=2");
        Assert.Equal(2, paged.GetProperty("total").GetInt32());
        Assert.Single(paged.GetProperty("items").EnumerateArray());

        foreach (var (query, field) in new[] { ("search=a", "search"), ("kind=Podcast", "kind"),
            ("search=" + new string('x', 101), "search"), (string.Join('&', Enumerable.Range(0, 11).Select(i => $"situation=s{i}")), "situation") })
        {
            var invalid = await anonymous.GetAsync("/api/learn/posts?" + query);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var problem = await invalid.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("VALIDATION_ERROR", problem.GetProperty("code").GetString());
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), query);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync("/api/learn/posts?pageSize=101")).StatusCode);
    }
}
