using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fire3D.Application.Ifc;

/// <summary>
/// BE#110 / Mobile#22: fet3d.package/1, the manifest of a Unity package build (per-floor AssetBundles, outage lighting bundles,
/// building_metadata.json, scenario.json). The package is identified by the manifest: the manifest and unity_package outputs both
/// name &lt;outputPrefix&gt;manifest.json, and every file is pinned by the SHA-256 listed here. Metadata of the manifest output is the
/// manifest content plus the runtime fields every package carries (buildTarget, protocolVersion, manifestSchemaVersion,
/// minRuntimeVersion, requiredCapabilities).
/// </summary>
public static partial class PackageManifest
{
    public const string SchemaVersion = "fet3d.package/1";
    public const string FileName = "manifest.json";
    public const int MaxFiles = 2000, MaxFloors = 500, MaxPathLength = 200;
    public const long MaxFileBytes = 2L * 1024 * 1024 * 1024, MaxManifestBytes = 2 * 1024 * 1024;

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*\z")]
    private static partial Regex PathPattern();
    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex HashPattern();
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z")]
    private static partial Regex VersionPattern();

    /// <summary>A relative path inside the attempt prefix: no "..", no leading slash, at most 200 characters.</summary>
    public static bool IsValidPath(string? path) => path is { Length: > 0 and <= MaxPathLength } && PathPattern().IsMatch(path) && !path.Split('/').Any(x => x is "." or "..");

    public static bool IsValidHash(string? hash) => hash is not null && HashPattern().IsMatch(hash);

    /// <summary>Problems of a manifest output's metadata, as (path, message); empty when valid.</summary>
    public static List<(string Path, string Message)> Validate(JsonNode? metadata, string? pinnedBuildTarget)
    {
        var issues = new List<(string, string)>();
        if (metadata is not JsonObject m) { issues.Add(("metadata", "The manifest metadata must be an object.")); return issues; }
        foreach (var name in new[] { "buildTarget", "protocolVersion", "manifestSchemaVersion", "unityVersion", "materialLibraryVersion" })
            if (Text(m[name]) is not { Length: > 0 and <= 100 }) issues.Add(($"metadata.{name}", "A nonempty value of at most 100 characters is required."));
        foreach (var name in new[] { "runtimeVersion", "minRuntimeVersion" })
            if (Text(m[name]) is not { } version || !VersionPattern().IsMatch(version)) issues.Add(($"metadata.{name}", "Use a three-part runtime version."));
        if (pinnedBuildTarget is not null && Text(m["buildTarget"]) is { } target && target != pinnedBuildTarget)
            issues.Add(("metadata.buildTarget", "The build target must be the one pinned by the package job."));
        if (m["requiredCapabilities"] is not JsonArray caps || caps.Count > 100 || caps.Any(x => Text(x) is not { Length: > 0 and <= 100 }))
            issues.Add(("metadata.requiredCapabilities", "Use an array of capability names."));
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (m["files"] is not JsonArray files || files.Count is 0 or > MaxFiles) issues.Add(("metadata.files", $"List 1-{MaxFiles} files."));
        else
            for (var i = 0; i < files.Count; i++)
            {
                var at = $"metadata.files[{i}]";
                if (files[i] is not JsonObject file) { issues.Add((at, "Each file must be an object.")); continue; }
                if (file.Any(x => x.Key is not ("path" or "sha256" or "size"))) issues.Add((at, "A file has only path, sha256 and size."));
                var path = Text(file["path"]);
                if (!IsValidPath(path)) issues.Add(($"{at}.path", "Use a relative path of letters, digits, '.', '_', '-' and '/' (at most 200 characters, no '..')."));
                else if (path == FileName) issues.Add(($"{at}.path", "manifest.json is not listed in its own files."));
                else if (!paths.Add(path!)) issues.Add(($"{at}.path", "Each path is listed once."));
                if (!IsValidHash(Text(file["sha256"]))) issues.Add(($"{at}.sha256", "Use the lowercase hex SHA-256 of the file."));
                if (file["size"] is not JsonValue size || !size.TryGetValue<long>(out var bytes) || bytes <= 0 || bytes > MaxFileBytes)
                    issues.Add(($"{at}.size", "Use the file size in bytes (positive, single upload)."));
            }
        if (m["floors"] is not JsonArray floors || floors.Count is 0 or > MaxFloors) issues.Add(("metadata.floors", $"List 1-{MaxFloors} floors."));
        else
            for (var i = 0; i < floors.Count; i++)
            {
                var at = $"metadata.floors[{i}]";
                if (floors[i] is not JsonObject floor) { issues.Add((at, "Each floor must be an object.")); continue; }
                if (Text(floor["bundle"]) is not { } bundle || !paths.Contains(bundle)) issues.Add(($"{at}.bundle", "The floor bundle must be one of the listed files."));
                if (floor["lightingBundle"] is { } lighting && (Text(lighting) is not { } light || !paths.Contains(light)))
                    issues.Add(($"{at}.lightingBundle", "The lighting bundle must be one of the listed files."));
            }
        return issues;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
