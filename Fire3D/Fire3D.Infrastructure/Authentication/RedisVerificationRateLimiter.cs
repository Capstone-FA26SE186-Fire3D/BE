using System.Security.Cryptography;
using System.Text;
using Fire3D.Application.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Fire3D.Infrastructure.Authentication;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";
    public string? Configuration { get; set; }
    public string KeyPrefix { get; set; } = "fire3d";
    public string? KeyHashSecret { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Atomic, cross-instance rate limits. Key material is HMACed and never contains email or IP text.</summary>
public sealed class RedisVerificationRateLimiter(IServiceProvider services,
    IOptions<RedisOptions> configured) : IEmailVerificationRateLimiter
{
    private const string Increment = """
        local value = redis.call('INCR', KEYS[1])
        if value == 1 then redis.call('EXPIRE', KEYS[1], ARGV[1]) end
        if value > tonumber(ARGV[2]) then return redis.call('TTL', KEYS[1]) end
        return 0
        """;
    private readonly RedisOptions options = configured.Value;

    public Task<VerificationRateLimitDecision> CheckRegistrationAsync(string email, string remoteIp, CancellationToken ct) =>
        CheckAsync("verification-request", email, remoteIp, ct);
    public Task<VerificationRateLimitDecision> CheckResendAsync(string email, string remoteIp, CancellationToken ct) =>
        CheckAsync("verification-request", email, remoteIp, ct);

    private async Task<VerificationRateLimitDecision> CheckAsync(string operation, string email, string remoteIp, CancellationToken ct)
    {
        var multiplexer = services.GetService<IConnectionMultiplexer>();
        if (!options.Enabled) return new(true);
        if (multiplexer is null || !multiplexer.IsConnected || string.IsNullOrWhiteSpace(options.KeyHashSecret))
            return new(false, Unavailable: true); // fail closed for account-creation and mail-amplification routes.
        try
        {
            var db = multiplexer.GetDatabase();
            var emailKey = Hash("email", email);
            var ipKey = Hash("ip", remoteIp);
            var retry = 0L;
            foreach (var (bucket, seconds, limit) in new[]
            {
                ("minute", 60, 1), ("hour", 3600, 3), ("day", 86400, 5)
            })
                retry = Math.Max(retry, (long)await db.ScriptEvaluateAsync(Increment,
                    [Key(operation, bucket, emailKey)], [seconds, limit]));
            foreach (var (bucket, seconds, limit) in new[]
            {
                ("minute", 60, 10), ("hour", 3600, 50)
            })
                retry = Math.Max(retry, (long)await db.ScriptEvaluateAsync(Increment,
                    [Key(operation, bucket, ipKey)], [seconds, limit]));
            return retry > 0 ? new(false, TimeSpan.FromSeconds(Math.Max(1, retry))) : new(true);
        }
        catch (RedisException)
        {
            return new(false, Unavailable: true);
        }
    }

    private RedisKey Key(string operation, string bucket, string identity) => $"{options.KeyPrefix}:verification:{operation}:{bucket}:{identity}";
    private string Hash(string kind, string value)
    {
        var input = Encoding.UTF8.GetBytes(kind + ":" + value.Trim().ToLowerInvariant());
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.KeyHashSecret!), input)).ToLowerInvariant();
    }
}
