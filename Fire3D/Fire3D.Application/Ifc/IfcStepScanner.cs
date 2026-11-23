using System.Text;
using System.Text.RegularExpressions;

namespace Fire3D.Application.Ifc;

/// <summary>
/// What the upload checks read from a STEP (ISO-10303-21) file while it is hashed: whether it starts with the STEP header and the
/// first schema named by FILE_SCHEMA in the HEADER section.
/// </summary>
public sealed partial record IfcStepSummary(bool StepHeader, string? Schema)
{
    /// <summary>Schemas the geometry worker reads (Docs ifc_ingestion_visual_interaction.md): IFC2X3, IFC4, IFC4X3 and their addenda.</summary>
    public static readonly string[] SupportedSchemas = ["IFC2X3", "IFC4", "IFC4X3"];

    /// <summary>
    /// True for IFC2X3, IFC4, IFC4X3 and their suffixed variants (IFC2X3_TC1, IFC4_ADD2_TC1, IFC4X3_ADD2), compared case-insensitively as
    /// STEP keywords are. IFC4X1/IFC4X2 (interim infrastructure schemas) and non-IFC schemas such as AP214 are not supported.
    /// </summary>
    public bool SchemaSupported => Schema is { } schema && SupportedSchemaName().IsMatch(schema);

    [GeneratedRegex("^(IFC2X3|IFC4|IFC4X3)(_[A-Z0-9_]+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SupportedSchemaName();
}

/// <summary>
/// Streaming reader of a STEP file: feed every chunk in order with <see cref="Append"/>, then read <see cref="Result"/>. Only the first
/// <see cref="HeaderLimit"/> bytes are kept (for the header); the rest of the file is never buffered, so a 500 MB model costs no memory.
/// </summary>
public sealed partial class IfcStepScanner
{
    /// <summary>The HEADER section of an exported IFC is a few hundred bytes; FILE_SCHEMA must appear within the first 64 KiB.</summary>
    public const int HeaderLimit = 64 * 1024;
    private readonly byte[] head = new byte[HeaderLimit];
    private int headLength;

    public void Append(ReadOnlySpan<byte> chunk)
    {
        if (headLength < head.Length)
        {
            var take = Math.Min(chunk.Length, head.Length - headLength);
            chunk[..take].CopyTo(head.AsSpan(headLength));
            headLength += take;
        }
    }

    public IfcStepSummary Result()
    {
        var prefix = head.AsSpan(0, headLength);
        return new(IfcObjectDigest.IsStep(prefix), ReadSchema(prefix));
    }

    /// <summary>Scans a whole file held in memory (tests and small fixtures).</summary>
    public static IfcStepSummary Scan(ReadOnlySpan<byte> bytes)
    {
        var scanner = new IfcStepScanner();
        scanner.Append(bytes);
        return scanner.Result();
    }

    // FILE_SCHEMA(('IFC4')); in the HEADER section, i.e. before the first ENDSEC. Latin-1 keeps every byte one character, so a binary
    // file (renamed zip) can never fail decoding; it simply has no match.
    private static string? ReadSchema(ReadOnlySpan<byte> prefix)
    {
        if (!IfcObjectDigest.IsStep(prefix)) return null;
        var text = Encoding.Latin1.GetString(prefix);
        var end = text.IndexOf("ENDSEC", StringComparison.OrdinalIgnoreCase);
        var match = FileSchema().Match(end < 0 ? text : text[..end]);
        return match.Success ? match.Groups[1].Value.Trim().ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"FILE_SCHEMA\s*\(\s*\(\s*'([^']{0,64})'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileSchema();
}
