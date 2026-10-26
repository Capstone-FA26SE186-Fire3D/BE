using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Fire3D.AuthTests;

public sealed partial class AuthIntegrationTests
{
    // BE#172: formatted runs, list item runs and dividers are stored as normalized and served to anonymous readers.
    [PostgresFact]
    public async Task Learn_rich_text_and_dividers_round_trip_to_public_readers()
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", (await LoginAsync()).AccessToken);
        var blocks = JsonNode.Parse("""
            [{"type":"paragraph","runs":[{"text":"Khi chuông vang, "},{"text":"đừng mở cửa","marks":["bold"]},{"text":" xem QCVN","link":"https://example.vn/qcvn"}]},
             {"type":"divider"},
             {"type":"list","items":["Bò thấp",{"runs":[{"text":"Không","color":"danger"},{"text":" dùng thang máy"}]}]}]
            """)!;
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/admin/learn/posts")
        { Content = JsonContent.Create(new { slug = "rich-text", publish = true, version = new { kind = "Article", title = "Định dạng", summary = "s", blocks } }) };
        create.Headers.Add("Idempotency-Key", "rich-text");
        var created = await client.SendAsync(create);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

        using var anonymous = factory!.CreateClient();
        var post = (await anonymous.GetFromJsonAsync<JsonObject>("/api/learn/posts/rich-text"))!;
        var stored = post["publishedVersion"]!["blocks"]!.AsArray();
        Assert.Equal("Khi chuông vang, đừng mở cửa xem QCVN", stored[0]!["text"]!.GetValue<string>());
        Assert.Equal("bold", stored[0]!["runs"]![1]!["marks"]![0]!.GetValue<string>());
        Assert.Equal("https://example.vn/qcvn", stored[0]!["runs"]![2]!["link"]!.GetValue<string>());
        Assert.Equal("divider", stored[1]!["type"]!.GetValue<string>());
        Assert.Equal("Không dùng thang máy", stored[2]!["items"]![1]!.GetValue<string>());
        Assert.Equal("danger", stored[2]!["itemRuns"]![1]![0]!["color"]!.GetValue<string>());

        var invalid = new HttpRequestMessage(HttpMethod.Post, "/api/admin/learn/posts")
        { Content = JsonContent.Create(new { slug = "rich-bad", version = new { kind = "Article", title = "t", summary = "s", blocks = JsonNode.Parse("""[{"type":"paragraph","runs":[{"text":"x","color":"#ff0000"}]}]""") } }) };
        invalid.Headers.Add("Idempotency-Key", "rich-bad");
        var rejected = await client.SendAsync(invalid);
        Assert.Equal((HttpStatusCode)422, rejected.StatusCode);
        var problem = (await rejected.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("CONTENT_INVALID", problem["code"]!.GetValue<string>());
        Assert.Equal("COLOR_UNSUPPORTED", problem["issues"]![0]!["code"]!.GetValue<string>());
    }
}
