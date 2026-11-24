using System.Text;
using System.Text.RegularExpressions;

namespace Fire3D.Application.Ifc;

/// <summary>
/// What the upload checks read from a STEP (ISO-10303-21) file while it is hashed: whether it starts with the STEP header, the first
/// schema named by FILE_SCHEMA in the HEADER section, and (#219) how many instances of the entity groups every usable building model has.
/// </summary>
public sealed partial record IfcStepSummary(bool StepHeader, string? Schema, IfcEntityCounts Counts)
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
/// #219 precheck: entity instances (<c>#id=NAME(</c>) per group. Doors are IFCDOOR and IFCDOORSTANDARDCASE (IFC4); walls and slabs are
/// every IFCWALL* / IFCSLAB* element entity (IFCWALLSTANDARDCASE, IFCSLABELEMENTEDCASE, ...) but not the IFCWALLTYPE/IFCSLABTYPE type objects.
/// </summary>
public sealed record IfcEntityCounts(long Storeys, long Spaces, long Doors, long WallsOrSlabs)
{
    /// <summary>Precheck groups with no instance, each with the QA code the 422 IFC_PRECHECK_FAILED answer carries.</summary>
    public IReadOnlyList<(string Group, string QaCode)> Missing()
    {
        var missing = new List<(string, string)>();
        if (Storeys == 0) missing.Add(("IfcBuildingStorey", "IFC_NO_STOREYS"));
        if (Spaces == 0) missing.Add(("IfcSpace", "IFC_NO_SPACES"));
        if (Doors == 0) missing.Add(("IfcDoor", "IFC_NO_DOORS"));
        if (WallsOrSlabs == 0) missing.Add(("IfcWall/IfcSlab", "IFC_NO_WALLS_OR_SLABS"));
        return missing;
    }
}

/// <summary>
/// Streaming reader of a STEP file: feed every chunk in order with <see cref="Append"/>, then read <see cref="Result"/>. Only the first
/// <see cref="HeaderLimit"/> bytes are kept (for the header); the entity count is a byte-by-byte state machine, so a 500 MB model is
/// scanned without being held in memory.
/// </summary>
public sealed partial class IfcStepScanner
{
    /// <summary>The HEADER section of an exported IFC is a few hundred bytes; FILE_SCHEMA must appear within the first 64 KiB.</summary>
    public const int HeaderLimit = 64 * 1024;
    // Longest entity name counted is IFCSLABELEMENTEDCASE / IFCWALLELEMENTEDCASE; longer names are never one of the groups.
    private const int MaxName = 40;
    private readonly byte[] head = new byte[HeaderLimit];
    private int headLength;
    private readonly byte[] name = new byte[MaxName];
    private int nameLength;
    private bool nameTooLong, nameEnded, afterEquals, inString, inComment;
    private byte previous;
    private long storeys, spaces, doors, wallsOrSlabs;

    public void Append(ReadOnlySpan<byte> chunk)
    {
        if (headLength < head.Length)
        {
            var take = Math.Min(chunk.Length, head.Length - headLength);
            chunk[..take].CopyTo(head.AsSpan(headLength));
            headLength += take;
        }
        foreach (var b in chunk) Step(b);
    }

    // STEP text outside strings and comments: '#12=IFCWALL(' (whitespace allowed around '=' and before '('). Strings ('...', with ''
    // as an escaped quote, which is two toggles) and /* comments */ never count, so a name such as 'IFCDOOR(' inside a label is ignored.
    private void Step(byte b)
    {
        if (inComment)
        {
            if (previous == (byte)'*' && b == (byte)'/') { inComment = false; b = 0; }
            previous = b;
            return;
        }
        if (inString)
        {
            if (b == (byte)'\'') inString = false;
            previous = b;
            return;
        }
        if (previous == (byte)'/' && b == (byte)'*') { inComment = true; ResetName(); previous = 0; return; }
        previous = b;
        var upper = b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b;
        var letter = upper is >= (byte)'A' and <= (byte)'Z';
        var identifier = letter || upper is >= (byte)'0' and <= (byte)'9' || upper == (byte)'_';
        var space = b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
        if (identifier && !nameEnded && (nameLength > 0 || nameTooLong || (afterEquals && letter)))
        {
            afterEquals = false;
            if (nameLength < MaxName) name[nameLength++] = upper; else nameTooLong = true;
            return;
        }
        if (space)
        {
            if (nameLength > 0 || nameTooLong) nameEnded = true;
            return;
        }
        if (b == (byte)'(' && nameLength > 0 && !nameTooLong) Count(name.AsSpan(0, nameLength));
        ResetName();
        afterEquals = b == (byte)'=';
        if (b == (byte)'\'') inString = true;
    }

    private void ResetName() { nameLength = 0; nameTooLong = false; nameEnded = false; afterEquals = false; }

    private void Count(ReadOnlySpan<byte> entity)
    {
        if (entity.SequenceEqual("IFCBUILDINGSTOREY"u8)) storeys++;
        else if (entity.SequenceEqual("IFCSPACE"u8)) spaces++;
        else if (entity.SequenceEqual("IFCDOOR"u8) || entity.SequenceEqual("IFCDOORSTANDARDCASE"u8)) doors++;
        else if ((entity.StartsWith("IFCWALL"u8) || entity.StartsWith("IFCSLAB"u8)) && !entity.EndsWith("TYPE"u8)) wallsOrSlabs++;
    }

    public IfcStepSummary Result()
    {
        var prefix = head.AsSpan(0, headLength);
        return new(IfcObjectDigest.IsStep(prefix), ReadSchema(prefix), new(storeys, spaces, doors, wallsOrSlabs));
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
