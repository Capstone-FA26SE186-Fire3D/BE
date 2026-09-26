namespace Fire3D.Application.Authentication.Avatar;

public sealed record AvatarValidationError(string Code, string Message);

public static class AvatarUploadRules
{
    public const long MaxBytes = 5 * 1024 * 1024;

    private static readonly HashSet<string> SupportedContentTypes = new(StringComparer.Ordinal)
    {
        "image/jpeg",
        "image/png",
        "image/webp"
    };

    public static AvatarValidationError? ValidateIntent(string? contentType, long contentLength)
    {
        if (contentLength is <= 0 or > MaxBytes)
            return new("INVALID_AVATAR_SIZE", "Avatar must be between 1 byte and 5 MiB.");

        if (string.IsNullOrWhiteSpace(contentType) || !SupportedContentTypes.Contains(contentType.Trim().ToLowerInvariant()))
            return new("INVALID_AVATAR_MEDIA_TYPE", "Avatar must be a JPEG, PNG, or WebP image.");

        return null;
    }

    public static AvatarValidationError? ValidateImageSignature(string contentType, ReadOnlySpan<byte> prefix)
    {
        var valid = contentType switch
        {
            "image/jpeg" => prefix.Length >= 3 && prefix[..3].SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF }),
            "image/png" => prefix.Length >= 8 && prefix[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/webp" => prefix.Length >= 12 && prefix[..4].SequenceEqual("RIFF"u8) && prefix.Slice(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
        return valid ? null : new("INVALID_AVATAR_CONTENT", "Avatar bytes do not match the declared image type.");
    }
}
