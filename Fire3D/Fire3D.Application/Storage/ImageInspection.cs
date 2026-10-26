using SkiaSharp;

namespace Fire3D.Application.Storage;

/// <summary>
/// Decodes an uploaded image completely before it is trusted: the real format matches the declared type, it has one frame,
/// and size and dimensions stay bounded. Shared by avatars and Learn media.
/// </summary>
public static class ImageInspection
{
    public const int MaxSide = 4096;

    /// <summary>Width and height of a valid single-frame JPEG, PNG or WebP of the declared type, otherwise null.</summary>
    public static (int Width, int Height)? Inspect(byte[]? bytes, string contentType, long maxBytes)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length > maxBytes) return null;
        using var input = new MemoryStream(bytes, writable: false);
        using var codec = SKCodec.Create(input);
        if (codec is null) return null;
        var mime = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Webp => "image/webp",
            _ => null
        };
        if (mime is null || !string.Equals(mime, contentType, StringComparison.OrdinalIgnoreCase)
            || codec.Info.Width is <= 0 or > MaxSide || codec.Info.Height is <= 0 or > MaxSide
            || codec.FrameCount > 1
            || (codec.EncodedFormat == SKEncodedImageFormat.Png && !HasSinglePngFrame(bytes))) return null;

        // Bound decoded memory before allocation. Success is required: a partially
        // decoded/truncated image must not become a stored, immutable source.
        using var pixels = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul));
        return pixels.GetPixels() != IntPtr.Zero
            && codec.GetPixels(pixels.Info, pixels.GetPixels()) == SKCodecResult.Success
            ? (codec.Info.Width, codec.Info.Height) : null;
    }

    private static bool HasSinglePngFrame(ReadOnlySpan<byte> bytes)
    {
        // Skia may decode an APNG's default image as static PNG. Inspect its
        // animation controls too, so unsupported animation cannot bypass FrameCount.
        var animation = false;
        var frames = 0;
        for (var offset = 8; offset <= bytes.Length - 12;)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > (uint)(bytes.Length - offset - 12)) return false;
            var type = bytes.Slice(offset + 4, 4);
            if (type.SequenceEqual("acTL"u8))
            {
                if (animation || length != 8
                    || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8, 4)) != 1) return false;
                animation = true;
            }
            else if (type.SequenceEqual("fcTL"u8))
            {
                if (!animation || length != 26 || ++frames > 1) return false;
            }
            else if (type.SequenceEqual("fdAT"u8)) return false; // An extra frame beyond the default image.
            offset += (int)length + 12;
            if (type.SequenceEqual("IEND"u8)) return length == 0 && offset == bytes.Length && (!animation || frames == 1);
        }
        return false;
    }
}
