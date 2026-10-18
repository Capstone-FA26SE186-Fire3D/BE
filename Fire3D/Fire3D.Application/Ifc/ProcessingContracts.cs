using System.Text.Json;
using Fire3D.Application.Authentication;
namespace Fire3D.Application.Ifc;
public sealed class ProcessingWorkerOptions
{
    public bool DispatcherEnabled { get; set; }
    public bool WorkerApiEnabled { get; set; }
    public string Transport { get; set; } = "Http";
    public bool ConsumerEnabled { get; set; }
    public string WorkerUrl { get; set; } = "";
    public string MachineKey { get; set; } = "";
    public string[] AllowedToolchains { get; set; } = [];
    public int PollSeconds { get; set; } = 5;
    /// <summary>BE#110: the Unity worker leases package jobs itself (outbound only); the push dispatcher then skips package jobs.</summary>
    public bool PackagePullEnabled { get; set; }
    /// <summary>Lease length of package jobs on claim and renew (60-900 seconds); a Unity bake takes tens of minutes.</summary>
    public int PackageLeaseSeconds { get; set; } = 300;
    /// <summary>The Geometry (IFC) worker leases Geometry jobs itself (outbound only); the push dispatcher then skips Geometry jobs.</summary>
    public bool GeometryPullEnabled { get; set; }
    /// <summary>Lease length of pulled Geometry attempts on lease and renew (60-900 seconds); push Geometry attempts keep 60 seconds.</summary>
    public int GeometryLeaseSeconds { get; set; } = 600;

    /// <summary>Lease lengths accepted for pulled jobs.</summary>
    public static bool IsValidLeaseSeconds(int seconds) => seconds is >= 60 and <= 900;
    /// <summary>
    /// A pull mode needs the worker API and cannot be combined with Redis delivery: the Redis publisher skips no job kind, so it would
    /// still push the pulled jobs.
    /// </summary>
    public bool IsValidPullMode(bool pull) => !pull || (WorkerApiEnabled && !(Transport == "RedisStreams" && (DispatcherEnabled || ConsumerEnabled)));
    /// <summary>Key passed to processing_dispatch_gate('Claim') so the HTTP dispatcher leaves pulled job kinds to their pull lease.</summary>
    public string? DispatchExclusion => (PackagePullEnabled, GeometryPullEnabled) switch
    {
        (true, true) => "ExcludePackagesAndGeometry",
        (true, false) => "ExcludePackages",
        (false, true) => "ExcludeGeometry",
        _ => null
    };
}
public interface IRevisionProcessingStore
{
    Task<AuthResult<Guid>> RequestAsync(Guid actor, Guid revision, string key, CancellationToken ct);
}
public interface IProcessingWorkerGate
{
    Task<AuthResult<JsonElement>> ExecuteAsync(string action, Guid job, JsonElement input, CancellationToken ct);
    /// <summary>Leases and claims the oldest deliverable package job; null when none is waiting.</summary>
    Task<AuthResult<JsonElement?>> LeasePackageAsync(JsonElement input, CancellationToken ct);
    /// <summary>Leases and claims the oldest deliverable Geometry job; null when none is waiting.</summary>
    Task<AuthResult<JsonElement?>> LeaseGeometryAsync(JsonElement input, CancellationToken ct);
    /// <summary>Worker context of a live attempt (null when the attempt or lease is not current).</summary>
    Task<JsonElement?> ContextAsync(Guid attempt, Guid leaseToken, CancellationToken ct);
}
/// <summary>Short-lived S3 URLs and checks for the package worker and package downloads; never returns credentials.</summary>
public interface IPackageStorage
{
    /// <summary>GET URL after a HEAD size check; null when the object is missing or its size differs.</summary>
    Task<SignedDownload?> SignVerifiedGetAsync(string key, long sizeBytes, TimeSpan lifetime, CancellationToken ct);
    Task<SignedDownload> SignGetAsync(string key, TimeSpan lifetime, CancellationToken ct);
    /// <summary>PUT URL that S3 accepts only with the given SHA-256 (x-amz-checksum-sha256); returns the headers the client must send.</summary>
    Task<SignedUpload> SignPutAsync(string key, string sha256Hex, TimeSpan lifetime, CancellationToken ct);
    /// <summary>Size and stored SHA-256 checksum (hex) of an object; null when missing.</summary>
    Task<(long Size, string? Sha256Hex)?> HeadAsync(string key, CancellationToken ct);
    /// <summary>Whole object when it is at most maxBytes, otherwise null.</summary>
    Task<byte[]?> ReadAsync(string key, long maxBytes, CancellationToken ct);
}
public sealed record SignedUpload(string Url, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);
/// <summary>Pinned fet3d.package/1 files of a started learner session or playtest.</summary>
public interface IRuntimePackageFiles
{
    Task<AuthResult<JsonElement>> GetAsync(string kind, Guid actor, Guid family, Guid id, CancellationToken ct);
}
public interface IProcessingDispatchGate
{
    Task<JsonElement> ExecuteAsync(string action, string? key, Guid? token, Guid? receipt, CancellationToken ct);
}
