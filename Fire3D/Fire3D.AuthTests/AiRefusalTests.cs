using System.Text.Json;
using System.Text.Json.Nodes;
using Fire3D.Infrastructure.Ai;
using Xunit;

namespace Fire3D.AuthTests;

/// <summary>BE#188: what the worker hands the gate when the AI service refuses the pinned input. No database.</summary>
public sealed class AiRefusalTests
{
    private static string Json(JsonObject? body, string? status) => JsonSerializer.Serialize(AiRefusal.Input(body, status));

    [Fact]
    public void The_code_message_and_named_document_are_passed_on_with_the_http_status()
    {
        var body = new JsonObject { ["code"] = "AI_SOURCE_HASH_MISMATCH", ["message"] = "Source content hash does not match the pinned hash", ["document_id"] = "0f8fad5b-d9cb-469f-a165-70867728950e" };
        Assert.Equal("""{"code":"AI_SOURCE_HASH_MISMATCH","message":"Source content hash does not match the pinned hash","documentId":"0f8fad5b-d9cb-469f-a165-70867728950e","httpStatus":"422"}""",
            Json(body, "422"));
    }

    [Fact]
    public void The_document_may_arrive_as_documentId_and_document_id_wins_when_both_are_sent()
    {
        Assert.Contains("\"documentId\":\"abc\"", Json(new JsonObject { ["code"] = "X", ["documentId"] = "abc" }, "400"));
        Assert.Contains("\"documentId\":\"first\"", Json(new JsonObject { ["document_id"] = "first", ["documentId"] = "second" }, "400"));
    }

    [Fact]
    public void A_refusal_without_a_readable_body_keeps_only_the_status()
    {
        Assert.Equal("""{"code":null,"message":null,"documentId":null,"httpStatus":"400"}""", Json(null, "400"));
        Assert.Equal("""{"code":null,"message":null,"documentId":null,"httpStatus":"422"}""", Json(new JsonObject(), "422"));
    }

    [Fact]
    public void Values_that_are_not_text_are_ignored()
    {
        var body = new JsonObject { ["code"] = 422, ["message"] = new JsonObject { ["x"] = 1 }, ["document_id"] = true };
        Assert.Equal("""{"code":null,"message":null,"documentId":null,"httpStatus":"422"}""", Json(body, "422"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   \r\n\t ", null)]
    [InlineData("plain text", "plain text")]
    [InlineData("  padded  ", "padded")]
    [InlineData("two\nlines\r\nand\ttabs", "two lines and tabs")]
    [InlineData("many     spaces", "many spaces")]
    [InlineData("bell\u0007 and nul\0 inside", "bell and nul inside")]
    public void Text_becomes_one_plain_line(string? input, string? expected) => Assert.Equal(expected, AiRefusal.Plain(input, 500));

    [Fact]
    public void Text_is_cut_to_the_limit_and_never_ends_with_a_space()
    {
        var cut = AiRefusal.Plain(new string('a', 600), AiRefusal.MessageMaxLength);
        Assert.Equal(AiRefusal.MessageMaxLength, cut!.Length);
        var spaced = AiRefusal.Plain("abcd efgh", 5);
        Assert.Equal("abcd", spaced);
        Assert.True(AiRefusal.Plain(string.Join(' ', Enumerable.Repeat("word", 200)), AiRefusal.MessageMaxLength)!.Length <= AiRefusal.MessageMaxLength);
    }

    [Fact]
    public void The_message_and_document_are_cut_before_they_reach_the_gate()
    {
        var body = new JsonObject { ["code"] = "AI_SOURCE_UNAVAILABLE", ["message"] = new string('m', 900), ["document_id"] = new string('d', 200) };
        using var parsed = JsonDocument.Parse(Json(body, "422"));
        Assert.Equal(AiRefusal.MessageMaxLength, parsed.RootElement.GetProperty("message").GetString()!.Length);
        Assert.Equal(AiRefusal.DocumentIdMaxLength, parsed.RootElement.GetProperty("documentId").GetString()!.Length);
    }
}
