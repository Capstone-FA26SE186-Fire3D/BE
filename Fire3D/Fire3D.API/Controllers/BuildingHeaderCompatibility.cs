namespace Fire3D.API.Controllers;

/// <summary>
/// Compatibility:BuildingHeadersOptional, read once at startup (an App Setting change restarts the app). While true, Building writes
/// without Idempotency-Key/If-Match are accepted with a Warning header (FE#48 transition); default false.
/// </summary>
public sealed class BuildingHeaderCompatibility
{
    public bool HeadersOptional { get; set; }
}
