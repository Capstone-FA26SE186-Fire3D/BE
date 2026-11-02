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

    public static object Input(JsonObject? body, string? httpStatus) => new
    {
        code = Text(body, "code"),
        message = Plain(Text(body, "message"), MessageMaxLength),
        documentId = Plain(Text(body, "document_id") ?? Text(body, "documentId"), DocumentIdMaxLength),
        httpStatus
    };

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
