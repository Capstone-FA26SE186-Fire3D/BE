using System.Text;
using System.Text.Json.Nodes;

namespace Fire3D.Infrastructure.Ai;

/// <summary>
/// What the AI service said when it refused the pinned input (400/422), as the gate stores it for Admin (BE#188). The service answers
/// {"code","message"} and may add the document it could not use as document_id, so a hash mismatch or an unreachable source can be traced to one
/// source instead of one of up to 100 pinned. Text is cut to plain, short strings: Admin reads it, nobody else does.
/// </summary>
internal static class AiRefusal
{
    public const int MessageMaxLength = 500;
    public const int DocumentIdMaxLength = 64;
    /// <summary>The AI service sends no Retry-After for sources still being prepared; embedding a large PDF takes minutes.</summary>
    public const int DefaultPreparingRetrySeconds = 120;
    public const string SourcesPreparing = "AI_SOURCES_PREPARING";

    public static object Input(JsonObject? body, string? httpStatus, int? retryAfterSeconds = null) => new
    {
        code = Text(body, "code"),
        message = Plain(Text(body, "message"), MessageMaxLength),
        documentId = Plain(Text(body, "document_id") ?? Text(body, "documentId"), DocumentIdMaxLength),
        httpStatus,
        retryAfterSeconds
    };

    /// <summary>Retry-After as whole seconds between 1 and 3600, from either its delay or its date form; null when absent.</summary>
    public static int? RetryAfter(System.Net.Http.Headers.RetryConditionHeaderValue? header)
    {
        var seconds = header?.Delta?.TotalSeconds ?? (header?.Date is { } date ? (date - DateTimeOffset.UtcNow).TotalSeconds : (double?)null);
        return seconds is null ? null : (int)Math.Clamp(Math.Ceiling(seconds.Value), 1, 3600);
    }

    private static string? Text(JsonObject? body, string name) =>
        body?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Control characters and runs of white space become one space; the result is trimmed and cut, and empty text is null.</summary>
    internal static string? Plain(string? text, int maxLength)
    {
        if (text is null) return null;
        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        var space = false;
        foreach (var c in text)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c)) { space = builder.Length > 0; continue; }
            if (space) { if (builder.Length + 1 >= maxLength) break; builder.Append(' '); space = false; }
            builder.Append(c);
            if (builder.Length >= maxLength) break;
        }
        return builder.Length == 0 ? null : builder.ToString();
    }
}
