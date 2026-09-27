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
    public string? Password { get; set; }
    public bool? Ssl { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Atomic, cross-instance rate limits. Key material is HMACed and never contains email or IP text.</summary>
public sealed class RedisVerificationRateLimiter(IServiceProvider services,
    IOptions<RedisOptions> configured) : IEmailVerificationRateLimiter
{
    private const string CheckAndIncrement = """
        local blocked = redis.call('TTL', KEYS[7])
        if blocked > 0 then return {2, blocked} end
        for i=1,5 do
          local value = tonumber(redis.call('GET', KEYS[i]) or '0')
          local limit = tonumber(ARGV[(i - 1) * 2 + 2])
          if value >= limit then
            local rejected = redis.call('INCR', KEYS[6])
            if rejected == 1 then redis.call('EXPIRE', KEYS[6], 300) end
            if rejected >= 20 then
              redis.call('SET', KEYS[7], '1', 'EX', 900)
              return {2, 900}
            end
            return {1, redis.call('TTL', KEYS[i])}
          end
        end
        for i=1,5 do
          local value = redis.call('INCR', KEYS[i])
          if value == 1 then redis.call('EXPIRE', KEYS[i], tonumber(ARGV[(i - 1) * 2 + 1])) end
        end
        return {0, 0}
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
            var scriptResult = await db.ScriptEvaluateAsync(CheckAndIncrement,
                [
                    Key(operation, "minute", emailKey), Key(operation, "hour", emailKey), Key(operation, "day", emailKey),
                    Key(operation, "minute", ipKey), Key(operation, "hour", ipKey),
                    Key(operation, "rejections", ipKey), Key(operation, "blocked", ipKey)
                ],
                [60, 1, 3600, 3, 86400, 5, 60, 10, 3600, 50]);
            var result = scriptResult.IsNull ? null : (RedisResult[]?)scriptResult;
            if (result is not { Length: 2 }) return new(false, Unavailable: true);
            var outcome = (int)result[0];
            var retry = Math.Max(1, (int)result[1]);
            return outcome == 0 ? new(true) : new(false, TimeSpan.FromSeconds(retry));
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
