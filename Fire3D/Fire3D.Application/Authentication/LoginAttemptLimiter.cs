using System.Collections.Concurrent;

namespace Fire3D.Application.Authentication;

/// <summary>Failed password sign-ins allowed per normalized email in one window (BE#141). Configured under LoginAttempts.</summary>
public sealed class LoginAttemptOptions
{
    public int MaxFailures { get; set; } = 10;
    public int WindowMinutes { get; set; } = 15;
}

/// <summary>
/// Throttles password guessing per account, not per IP, so users behind one proxy or classroom NAT do not block each other.
/// Unknown emails are counted the same way, so the response never reveals whether an account exists.
/// </summary>
public interface ILoginAttemptLimiter
{
    /// <summary>Seconds until the email may try again, or null when sign-in is allowed.</summary>
    int? RetryAfterSeconds(string email, DateTime now);
    void RecordFailure(string email, DateTime now);
    void Reset(string email);
}

/// <summary>In-process fixed window per email; a multi-instance deployment needs a shared store.</summary>
public sealed class MemoryLoginAttemptLimiter(LoginAttemptOptions options) : ILoginAttemptLimiter
{
    private readonly ConcurrentDictionary<string, (int Failures, DateTime WindowStart)> windows = new(StringComparer.Ordinal);
    private TimeSpan Window => TimeSpan.FromMinutes(Math.Max(1, options.WindowMinutes));

    public int? RetryAfterSeconds(string email, DateTime now)
    {
        if (!windows.TryGetValue(email, out var entry)) return null;
        var remaining = entry.WindowStart + Window - now;
        if (remaining <= TimeSpan.Zero) { windows.TryRemove(KeyValuePair.Create(email, entry)); return null; }
        return entry.Failures >= Math.Max(1, options.MaxFailures) ? Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds)) : null;
    }

    public void RecordFailure(string email, DateTime now)
    {
        windows.AddOrUpdate(email, (1, now), (_, entry) => entry.WindowStart + Window <= now ? (1, now) : (entry.Failures + 1, entry.WindowStart));
        // Bound memory under spraying: drop expired windows once the table grows.
        if (windows.Count > 10_000)
            foreach (var pair in windows)
                if (pair.Value.WindowStart + Window <= now) windows.TryRemove(pair);
    }

    public void Reset(string email) => windows.TryRemove(email, out _);
}
