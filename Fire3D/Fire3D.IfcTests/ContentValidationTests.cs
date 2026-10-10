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
