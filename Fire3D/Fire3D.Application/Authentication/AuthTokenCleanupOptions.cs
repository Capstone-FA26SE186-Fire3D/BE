namespace Fire3D.Application.Authentication;

public sealed class AuthTokenCleanupOptions
{
    public const string SectionName = "AuthTokenCleanup";

    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 60;
    public int RetentionDays { get; set; } = 7;
    public int BatchSize { get; set; } = 500;
    public int MaxBatchesPerRun { get; set; } = 10;

    public bool IsValid() =>
        IntervalMinutes is >= 1 and <= 1440 &&
        RetentionDays is >= 1 and <= 365 &&
        BatchSize is >= 1 and <= 1000 &&
        MaxBatchesPerRun is >= 1 and <= 100;
}

public interface IRefreshTokenCleanupStore
{
    Task<int> DeleteExpiredFamiliesAsync(int retentionDays, int batchSize, CancellationToken ct);
}
