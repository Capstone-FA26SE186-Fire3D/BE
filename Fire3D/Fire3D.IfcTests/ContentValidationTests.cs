using System.Text.Json.Nodes;
using Fire3D.Application.Content;
using Xunit;

namespace Fire3D.IfcTests;

public sealed class ContentValidationTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=10", "YouTube", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "YouTube", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/shorts/dQw4w9WgXcQ", "YouTube", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.tiktok.com/@fire.safety/video/7234567890123456789", "TikTok", "https://www.tiktok.com/@fire.safety/video/7234567890123456789")]
    [InlineData("https://www.facebook.com/firepage/videos/1234567890123/", "Facebook", "https://www.facebook.com/watch/?v=1234567890123")]
    [InlineData("https://www.facebook.com/watch/?v=1234567890123", "Facebook", "https://www.facebook.com/watch/?v=1234567890123")]
    public void Allowlisted_video_urls_are_canonicalised(string url, string provider, string canonical)
    {
        var media = LearnContent.Canonicalize(url);
        Assert.Equal(provider, media?.Provider);
        Assert.Equal(canonical, media?.CanonicalUrl);
    }

    [Theory]
    [InlineData("http://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://fb.watch/abcdef/")]
    [InlineData("https://vimeo.com/123456")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://user:pass@www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("<iframe src=\"https://www.youtube.com/embed/dQw4w9WgXcQ\"></iframe>")]
    public void Other_urls_are_rejected(string url) => Assert.Null(LearnContent.Canonicalize(url));

    [Fact]
    public void Content_is_plain_text_strict_and_canonical()
    {
        var input = new LearnVersionInput("Video", "Cách thoát hiểm", "Tóm tắt", null, new JsonArray(
            JsonNode.Parse("""{"type":"heading","text":"Bước 1","level":2}"""),
            JsonNode.Parse("""{"type":"paragraph","text":"Đi theo biển báo."}"""),
            JsonNode.Parse("""{"type":"video","url":"https://youtu.be/dQw4w9WgXcQ"}""")));
        var (issues, content) = LearnContent.Normalize(input);
        Assert.Empty(issues);
        Assert.Equal("fet3d.learn/1", content!["contentSchemaVersion"]!.GetValue<string>());
        Assert.Equal("dQw4w9WgXcQ", content["blocks"]![2]!["videoId"]!.GetValue<string>());

        var bad = new LearnVersionInput("Video", "<script>alert(1)</script>", "ok", "http://insecure.example/x.png", new JsonArray(
            JsonNode.Parse("""{"type":"paragraph","text":"<iframe src=x>","style":"x"}"""),
            JsonNode.Parse("""{"type":"html","text":"x"}"""),
            JsonNode.Parse("""{"type":"video","url":"https://vimeo.com/1"}""")));
        var codes = LearnContent.Normalize(bad).Issues.Select(x => (x.Code, x.Path)).ToList();
        Assert.Contains(("MARKUP_NOT_ALLOWED", "$.title"), codes);
        Assert.Contains(("URL_INVALID", "$.coverImageUrl"), codes);
        Assert.Contains(("FIELD_UNKNOWN", "$.blocks[0].style"), codes);
        Assert.Contains(("MARKUP_NOT_ALLOWED", "$.blocks[0].text"), codes);
        Assert.Contains(("VALUE_UNSUPPORTED", "$.blocks[1].type"), codes);
        Assert.Contains(("LEARN_MEDIA_INVALID", "$.blocks[2].url"), codes);
        Assert.Contains(("VIDEO_REQUIRED", "$.blocks"), codes);
        Assert.Equal("1 < 2", LearnContent.Normalize(new("Tip", "a", "b", null, new JsonArray(JsonNode.Parse("""{"type":"paragraph","text":"1 < 2"}""")))).Content!["blocks"]![0]!["text"]!.GetValue<string>());
    }

    private static void Json(string expected, JsonNode? actual) => Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), actual?.ToJsonString());

    [Fact]
    public void Rich_text_runs_keep_plain_text_and_canonical_formatting()
    {
        // BE#172: formatted runs, theme colour tokens, https links and a divider; plain text stays on every text block.
        var input = new LearnVersionInput("Article", "Báo cháy", "Tóm tắt", null, new JsonArray(
            JsonNode.Parse("""{"type":"paragraph","runs":[{"text":"Khi chuông vang, "},{"text":"đừng mở cửa","marks":["bold"]},{"text":" "},{"text":"mu bàn tay","marks":["underline","italic","italic"]},{"text":" nếu nóng","color":"ember","highlight":"default"},{"text":" QCVN 06:2022","link":"https://example.vn/qcvn"}]}"""),
            JsonNode.Parse("""{"type":"heading","level":3,"runs":[{"text":"Bước "},{"text":"1","color":"danger"}]}"""),
            JsonNode.Parse("""{"type":"list","ordered":true,"items":["Bò thấp",{"runs":[{"text":"Không ","marks":["strike"]},{"text":"dùng thang máy"}]}]}"""),
            JsonNode.Parse("""{"type":"divider"}"""),
            JsonNode.Parse("""{"type":"callout","tone":"warning","runs":[{"text":"Hai "},{"text":"phần","color":"default"}]}""")));
        var (issues, content) = LearnContent.Normalize(input);
        Assert.Empty(issues);
        var blocks = content!["blocks"]!.AsArray();
        Assert.Equal("Khi chuông vang, đừng mở cửa mu bàn tay nếu nóng QCVN 06:2022", blocks[0]!["text"]!.GetValue<string>());
        var runs = blocks[0]!["runs"]!.AsArray();
        Assert.Equal(6, runs.Count);
        Json("""["bold"]""", runs[1]!["marks"]!);
        Json("""["italic","underline"]""", runs[3]!["marks"]!);
        Assert.Equal("ember", runs[4]!["color"]!.GetValue<string>());
        Assert.Null(runs[4]!["highlight"]);
        Assert.Equal("https://example.vn/qcvn", runs[5]!["link"]!.GetValue<string>());
        Assert.Equal(3, blocks[1]!["level"]!.GetValue<int>());
        Assert.Equal("Bước 1", blocks[1]!["text"]!.GetValue<string>());
        Json("""["Bò thấp","Không dùng thang máy"]""", blocks[2]!["items"]!);
        Json("""[[{"text":"Bò thấp"}],[{"text":"Không ","marks":["strike"]},{"text":"dùng thang máy"}]]""", blocks[2]!["itemRuns"]!);
        Json("""{"type":"divider"}""", blocks[3]!);
        // Unformatted runs collapse to plain text, so they store and hash exactly like the text form.
        Json("""{"type":"callout","tone":"warning","text":"Hai phần"}""", blocks[4]!);
        var plain = LearnContent.Normalize(new("Tip", "a", "b", null, new JsonArray(JsonNode.Parse("""{"type":"paragraph","text":"Hai phần"}""")))).Content!;
        var asRuns = LearnContent.Normalize(new("Tip", "a", "b", null, new JsonArray(JsonNode.Parse("""{"type":"paragraph","runs":[{"text":"Hai "},{"text":"phần"}]}""")))).Content!;
        Assert.Equal(plain.ToJsonString(), asRuns.ToJsonString());
        // Adjacent runs with the same formatting merge.
        var merged = LearnContent.Normalize(new("Tip", "a", "b", null, new JsonArray(JsonNode.Parse("""{"type":"paragraph","runs":[{"text":"A","marks":["bold"]},{"text":"B","marks":["bold"]},{"text":"C"}]}""")))).Content!;
        Json("""[{"text":"AB","marks":["bold"]},{"text":"C"}]""", merged["blocks"]![0]!["runs"]!);
    }

    [Fact]
    public void Rich_text_rejects_markup_free_colours_bad_links_and_mixed_forms()
    {
        var tooMany = new JsonArray(Enumerable.Range(0, LearnContent.MaxRuns + 1).Select(_ => (JsonNode)new JsonObject { ["text"] = "x" }).ToArray());
        var bad = new LearnVersionInput("Article", "a", "b", null, new JsonArray(
            JsonNode.Parse("""{"type":"paragraph","runs":[{"text":"<b>x</b>"},{"text":"y","marks":["shout"]},{"text":"z","color":"#ff0000","highlight":"pink"},{"text":"w","link":"http://example.vn"},{"text":"v","link":"https://user:pw@example.vn"},{"text":"u","style":"x"}]}"""),
            JsonNode.Parse("""{"type":"paragraph","text":"a","runs":[{"text":"a"}]}"""),
            JsonNode.Parse("""{"type":"divider","text":"---"}"""),
            JsonNode.Parse("""{"type":"callout","tone":"info","runs":[]}"""),
            new JsonObject { ["type"] = "paragraph", ["runs"] = tooMany },
            JsonNode.Parse("""{"type":"list","items":[{"runs":[{"text":"a"}],"text":"a"}]}"""),
            JsonNode.Parse("""{"type":"paragraph","runs":[{"text":"   "}]}""")));
        var codes = LearnContent.Normalize(bad).Issues.Select(x => (x.Code, x.Path)).ToList();
        Assert.Contains(("MARKUP_NOT_ALLOWED", "$.blocks[0].runs[0].text"), codes);
        Assert.Contains(("MARK_UNSUPPORTED", "$.blocks[0].runs[1].marks[0]"), codes);
        Assert.Contains(("COLOR_UNSUPPORTED", "$.blocks[0].runs[2].color"), codes);
        Assert.Contains(("COLOR_UNSUPPORTED", "$.blocks[0].runs[2].highlight"), codes);
        Assert.Contains(("URL_INVALID", "$.blocks[0].runs[3].link"), codes);
        Assert.Contains(("URL_INVALID", "$.blocks[0].runs[4].link"), codes);
        Assert.Contains(("FIELD_UNKNOWN", "$.blocks[0].runs[5].style"), codes);
        Assert.Contains(("TEXT_OR_RUNS", "$.blocks[1]"), codes);
        Assert.Contains(("FIELD_UNKNOWN", "$.blocks[2].text"), codes);
        Assert.Contains(("RUNS_INVALID", "$.blocks[3].runs"), codes);
        Assert.Contains(("RUNS_INVALID", "$.blocks[4].runs"), codes);
        Assert.Contains(("FIELD_UNKNOWN", "$.blocks[5].items[0].text"), codes);
        Assert.Contains(("STRING_INVALID", "$.blocks[6].runs"), codes);
    }

    [Fact]
    public void Library_payloads_are_validated_per_kind()
    {
        var rubric = JsonNode.Parse("""{"rubric":{"schema_version":"1","pass_threshold":1,"criteria":[{"id":"exit","metric":"reached_exit","mandatory":true,"weight":1,"operator":"eq","threshold":1}]}}""")!.AsObject();
        Assert.Empty(LibraryContent.Validate("RubricSample", new("Sample", rubric)));
        var unsupported = JsonNode.Parse("""{"rubric":{"schema_version":"1","pass_threshold":1,"criteria":[{"id":"exit","metric":"client_score","mandatory":true,"weight":1,"operator":"eq","threshold":1}]}}""")!.AsObject();
        Assert.Contains(LibraryContent.Validate("RubricSample", new("Sample", unsupported)), x => x.Code == "RUBRIC_METRIC_UNSUPPORTED" && x.Path == "$.payload.rubric.criteria[0].metric");
        Assert.Contains(LibraryContent.Validate("Equipment", new("Extinguisher", JsonNode.Parse("""{"description":"CO2","enablesCapability":"fire.source"}""")!.AsObject())), x => x.Code == "FIELD_UNKNOWN");
        Assert.Contains(LibraryContent.Validate("ScenarioTemplate", new("Template", JsonNode.Parse("""{"state":{"spawnPoints":[]}}""")!.AsObject())), x => x.Code == "EDITOR_SCHEMA_VERSION_UNSUPPORTED");
        Assert.Empty(LibraryContent.Validate("ScenarioTemplate", new("Template", new JsonObject { ["state"] = Fire3D.Tests.Shared.EditorContractFixtures.State() })));
        Assert.Contains(LibraryContent.Validate("Equipment", new("E", new JsonObject(), ["ok", "ok"])), x => x.Code == "CAPABILITIES_INVALID");
    }
}
