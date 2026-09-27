using System.Globalization;

namespace Fire3D.Application.Authentication;

public static class ProfileEtag
{
    public static string Format(long revision) => $"\"{revision.ToString(CultureInfo.InvariantCulture)}\"";

    public static bool TryParse(string? value, out long revision)
    {
        revision = 0;
        var tag = value?.Trim();
        return tag is { Length: > 2 } && tag[0] == '"' && tag[^1] == '"'
            && long.TryParse(tag.AsSpan(1, tag.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out revision)
            && revision > 0;
    }
}
