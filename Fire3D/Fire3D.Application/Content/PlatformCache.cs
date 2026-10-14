namespace Fire3D.Application.Content;

public sealed class PlatformCacheOptions
{
    public const string Section = "PlatformCache";
    public bool WorkerEnabled { get; set; } = true;
    public int PollSeconds { get; set; } = 5;
    public int MaxEventsPerRun { get; set; } = 100;
}

/// <summary>
/// Cache-aside version per content scope (learn, library, knowledge). Readers put the version in their cache keys, so one bump
/// retires every cached entry of the scope. Returns the receipt reference recorded with the outbox event.
/// </summary>
public interface IPlatformCacheVersions
{
    Task<string> BumpAsync(string scope, CancellationToken ct);
}
