using System.Data;
using System.Security.Cryptography;
using System.Text;
using Fire3D.Application.Authentication;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fire3D.Infrastructure.Authentication;

/// <summary>
/// PostgreSQL-backed proof-of-email flow used before an account exists. OTP and registration proof values are never
/// persisted in plaintext; the delivery job can decrypt only the still-active challenge it leases.
/// </summary>
public sealed class RegistrationOtpStore(Fire3DDbContext db, IOptions<JwtOptions> jwt, TimeProvider clock)
    : IRegistrationOtpService, IRegistrationOtpDeliveryQueue
{
    private static readonly TimeSpan OtpLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RegistrationTokenLifetime = TimeSpan.FromMinutes(15);
    private readonly byte[] key = DeriveKey(jwt.Value);

    public async Task<AuthResult<bool>> RequestOtpAsync(string email, string? remoteAddress, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockEmailAsync(email, ct);

        // Always accept an existing email without queuing a job so this endpoint does not reveal account existence.
        if (await db.Users.AsNoTracking().AnyAsync(user => user.Email == email, ct))
        {
            await tx.CommitAsync(ct);
            return AuthResult<bool>.Ok(true);
        }

        var oneMinuteAgo = clock.GetUtcNow().UtcDateTime.AddMinutes(-1);
        var recent = await ScalarAsync<bool>("""
            SELECT EXISTS(SELECT 1 FROM public.registration_otp_email_jobs
              WHERE email=@email AND created_at>@cutoff)
            """, [Param("email", email), Param("cutoff", oneMinuteAgo)], ct);
        if (recent)
        {
            await tx.CommitAsync(ct);
            return AuthResult<bool>.Ok(true);
        }

        var oneHourAgo = clock.GetUtcNow().UtcDateTime.AddHours(-1);
        var emailCount = await ScalarAsync<long>("""
            SELECT count(*) FROM public.registration_otp_email_jobs WHERE email=@email AND created_at>@cutoff
            """, [Param("email", email), Param("cutoff", oneHourAgo)], ct);
        var ipCount = string.IsNullOrWhiteSpace(remoteAddress) ? 0L : await ScalarAsync<long>("""
            SELECT count(*) FROM public.registration_otp_email_jobs WHERE remote_address=@remoteAddress AND created_at>@cutoff
            """, [Param("remoteAddress", remoteAddress), Param("cutoff", oneHourAgo)], ct);
        if (emailCount >= 5 || ipCount >= 20)
        {
            await tx.CommitAsync(ct);
            return AuthResult<bool>.Fail("OTP_RATE_LIMITED", "Too many verification requests. Please try again later.", 429);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await ExecuteAsync("""
            UPDATE public.registration_email_challenges SET superseded_at=@now
             WHERE email=@email AND superseded_at IS NULL
            """, [Param("email", email), Param("now", now)], ct);

        var challengeId = Guid.NewGuid();
        var otp = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var encryptedOtp = Encrypt(otp);
        await ExecuteAsync("""
            INSERT INTO public.registration_email_challenges
              (id,email,otp_hash,otp_ciphertext,otp_nonce,otp_tag,expires_at,created_at,updated_at)
            VALUES (@id,@email,@hash,@ciphertext,@nonce,@tag,@expiresAt,@now,@now)
            """, [
                Param("id", challengeId), Param("email", email), Param("hash", HashOtp(challengeId, otp)),
                Param("ciphertext", encryptedOtp.Ciphertext), Param("nonce", encryptedOtp.Nonce), Param("tag", encryptedOtp.Tag),
                Param("expiresAt", now.Add(OtpLifetime)), Param("now", now)], ct);
        await ExecuteAsync("""
            INSERT INTO public.registration_otp_email_jobs(id,challenge_id,email,remote_address,status,attempts,available_at,created_at)
            VALUES (@id,@challengeId,@email,@remoteAddress,'Pending',0,@now,@now)
            """, [Param("id", Guid.NewGuid()), Param("challengeId", challengeId), Param("email", email),
                Param("remoteAddress", (object?)remoteAddress ?? DBNull.Value), Param("now", now)], ct);
        await tx.CommitAsync(ct);
        return AuthResult<bool>.Ok(true);
    }

    public async Task<AuthResult<RegistrationOtpVerificationResponse>> VerifyOtpAsync(string email, string otp, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockEmailAsync(email, ct);
        var challenge = await ReadActiveChallengeAsync(email, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (challenge is null || challenge.ExpiresAt <= now)
        {
            await tx.CommitAsync(ct);
            return InvalidOtp();
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(challenge.OtpHash), Convert.FromHexString(HashOtp(challenge.Id, otp))))
        {
            var attempts = challenge.FailedAttempts + 1;
            await ExecuteAsync("""
                UPDATE public.registration_email_challenges
                SET failed_attempts=@attempts, superseded_at=CASE WHEN @attempts>=5 THEN @now ELSE superseded_at END, updated_at=@now
                WHERE id=@id
                """, [Param("attempts", attempts), Param("now", now), Param("id", challenge.Id)], ct);
            await tx.CommitAsync(ct);
            return InvalidOtp();
        }

        if (challenge.RegistrationTokenUsedAt.HasValue || challenge.RegistrationTokenExpiresAt <= now)
        {
            await tx.CommitAsync(ct);
            return AuthResult<RegistrationOtpVerificationResponse>.Fail("REGISTRATION_PROOF_EXPIRED",
                "Request a new verification code before registering.", 400);
        }

        if (challenge.VerifiedAt.HasValue)
        {
            var token = Decrypt(challenge.RegistrationTokenCiphertext!, challenge.RegistrationTokenNonce!, challenge.RegistrationTokenTag!);
            await tx.CommitAsync(ct);
            return AuthResult<RegistrationOtpVerificationResponse>.Ok(new(token, challenge.RegistrationTokenExpiresAt!.Value));
        }

        var registrationToken = Base64Url(RandomNumberGenerator.GetBytes(32));
        var encryptedToken = Encrypt(registrationToken);
        var expiresAt = now.Add(RegistrationTokenLifetime);
        await ExecuteAsync("""
            UPDATE public.registration_email_challenges
            SET verified_at=@now, registration_token_hash=@tokenHash, registration_token_ciphertext=@ciphertext,
                registration_token_nonce=@nonce, registration_token_tag=@tag, registration_token_expires_at=@expiresAt, updated_at=@now
            WHERE id=@id
            """, [Param("now", now), Param("tokenHash", HashToken(registrationToken)), Param("ciphertext", encryptedToken.Ciphertext),
                Param("nonce", encryptedToken.Nonce), Param("tag", encryptedToken.Tag), Param("expiresAt", expiresAt), Param("id", challenge.Id)], ct);
        await tx.CommitAsync(ct);
        return AuthResult<RegistrationOtpVerificationResponse>.Ok(new(registrationToken, expiresAt));
    }

    public async Task<AuthResult<bool>> ConsumeRegistrationTokenAsync(string email, string registrationToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(registrationToken))
            return AuthResult<bool>.Fail("EMAIL_VERIFICATION_REQUIRED", "Verify your email before creating an account.", 400);
        await LockEmailAsync(email, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var consumed = await ExecuteAsync("""
            UPDATE public.registration_email_challenges
               SET registration_token_used_at=@now, updated_at=@now
             WHERE email=@email AND registration_token_hash=@tokenHash AND verified_at IS NOT NULL
               AND superseded_at IS NULL AND registration_token_used_at IS NULL AND registration_token_expires_at>@now
            """, [Param("now", now), Param("email", email), Param("tokenHash", HashToken(registrationToken))], ct);
        return consumed == 1
            ? AuthResult<bool>.Ok(true)
            : AuthResult<bool>.Fail("EMAIL_VERIFICATION_REQUIRED", "Verify your email before creating an account.", 400);
    }

    public async Task<RegistrationOtpEmailJob?> ClaimAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await ExecuteAsync("""
                UPDATE public.registration_otp_email_jobs j SET status='Dead',lease_token=NULL,lease_until=NULL
                 WHERE j.status IN ('Pending','Leased') AND ((j.status='Leased' AND j.lease_until<=@now AND j.attempts>=5)
                    OR EXISTS(SELECT 1 FROM public.registration_email_challenges c WHERE c.id=j.challenge_id
                              AND (c.superseded_at IS NOT NULL OR c.verified_at IS NOT NULL OR c.expires_at<=@now)))
                """, [Param("now", now)], ct);
            var lease = Guid.NewGuid();
            await using var command = Command("""
                WITH candidate AS (
                  SELECT j.id FROM public.registration_otp_email_jobs j
                  JOIN public.registration_email_challenges c ON c.id=j.challenge_id
                  WHERE j.attempts<5 AND c.superseded_at IS NULL AND c.verified_at IS NULL AND c.expires_at>@now
                    AND ((j.status='Pending' AND j.available_at<=@now) OR (j.status='Leased' AND j.lease_until<=@now))
                  ORDER BY j.created_at FOR UPDATE SKIP LOCKED LIMIT 1)
                UPDATE public.registration_otp_email_jobs j SET status='Leased',lease_token=@lease,
                  lease_until=@leaseUntil,attempts=attempts+1 FROM candidate x WHERE j.id=x.id
                RETURNING j.id,j.email,j.lease_token,j.attempts,j.challenge_id
                """);
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("lease", lease);
            command.Parameters.AddWithValue("leaseUntil", now.AddMinutes(2));
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var jobId = reader.GetGuid(0);
            var email = reader.GetString(1);
            var leaseToken = reader.GetGuid(2);
            var attempts = reader.GetInt32(3);
            var challengeId = reader.GetGuid(4);
            await reader.DisposeAsync();
            var otp = await ReadOtpForLeaseAsync(challengeId, ct);
            return otp is null ? null : new RegistrationOtpEmailJob(jobId, email, otp, leaseToken, attempts);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    public Task CompleteAsync(RegistrationOtpEmailJob job, CancellationToken ct) => ExecuteAsync("""
        UPDATE public.registration_otp_email_jobs SET status='Succeeded',lease_token=NULL,lease_until=NULL
        WHERE id=@id AND status='Leased' AND lease_token=@lease
        """, [Param("id", job.Id), Param("lease", job.LeaseToken)], ct);

    public async Task FailAsync(RegistrationOtpEmailJob job, bool permanent, CancellationToken ct)
    {
        var availableAt = clock.GetUtcNow().UtcDateTime.AddSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Max(0, job.Attempt - 1))));
        await ExecuteAsync("""
            UPDATE public.registration_otp_email_jobs
            SET status=CASE WHEN @permanent OR attempts>=5 THEN 'Dead' ELSE 'Pending' END,
                available_at=@availableAt,lease_token=NULL,lease_until=NULL
            WHERE id=@id AND status='Leased' AND lease_token=@lease
            """, [Param("permanent", permanent), Param("availableAt", availableAt), Param("id", job.Id), Param("lease", job.LeaseToken)], ct);
    }

    private async Task<Challenge?> ReadActiveChallengeAsync(string email, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = Command("""
                SELECT id,otp_hash,failed_attempts,expires_at,verified_at,registration_token_expires_at,registration_token_used_at,
                       registration_token_ciphertext,registration_token_nonce,registration_token_tag
                FROM public.registration_email_challenges
                WHERE email=@email AND superseded_at IS NULL
                ORDER BY created_at DESC FOR UPDATE LIMIT 1
                """);
            command.Parameters.AddWithValue("email", email);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new Challenge(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4), reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6), reader.IsDBNull(7) ? null : (byte[])reader[7],
                reader.IsDBNull(8) ? null : (byte[])reader[8], reader.IsDBNull(9) ? null : (byte[])reader[9]);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private async Task<string?> ReadOtpForLeaseAsync(Guid challengeId, CancellationToken ct)
    {
        await using var command = Command("""
            SELECT otp_ciphertext,otp_nonce,otp_tag FROM public.registration_email_challenges
             WHERE id=@id AND superseded_at IS NULL AND verified_at IS NULL AND expires_at>@now
            """);
        command.Parameters.AddWithValue("id", challengeId);
        command.Parameters.AddWithValue("now", clock.GetUtcNow().UtcDateTime);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Decrypt((byte[])reader[0], (byte[])reader[1], (byte[])reader[2]) : null;
    }

    private async Task LockEmailAsync(string email, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"fet3d:registration-otp:" + email}, 0))", ct);

    private async Task<int> ExecuteAsync(string sql, IEnumerable<NpgsqlParameter> parameters, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = Command(sql);
            command.Parameters.AddRange(parameters.ToArray());
            return await command.ExecuteNonQueryAsync(ct);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private async Task<T> ScalarAsync<T>(string sql, IEnumerable<NpgsqlParameter> parameters, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = Command(sql);
            command.Parameters.AddRange(parameters.ToArray());
            var value = await command.ExecuteScalarAsync(ct);
            return (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private NpgsqlCommand Command(string sql) => new(sql, (NpgsqlConnection)db.Database.GetDbConnection())
    {
        Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction
    };

    private static NpgsqlParameter Param(string name, object? value) => new(name, value ?? DBNull.Value);
    private static AuthResult<RegistrationOtpVerificationResponse> InvalidOtp() =>
        AuthResult<RegistrationOtpVerificationResponse>.Fail("INVALID_OTP", "The verification code is invalid, expired or has already been used.", 400);
    private string HashOtp(Guid challengeId, string otp) => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(challengeId + ":" + otp))).ToLowerInvariant();
    private string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    private EncryptedValue Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var input = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[input.Length];
        var tag = new byte[16];
        using var cipher = new AesGcm(key, 16);
        cipher.Encrypt(nonce, input, ciphertext, tag);
        return new(ciphertext, nonce, tag);
    }
    private string Decrypt(byte[] ciphertext, byte[] nonce, byte[] tag)
    {
        var plaintext = new byte[ciphertext.Length];
        using var cipher = new AesGcm(key, 16);
        cipher.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
    private static byte[] DeriveKey(JwtOptions options)
    {
        var signingKey = Convert.FromBase64String(options.SigningKey);
        return SHA256.HashData(Encoding.UTF8.GetBytes("FET3D registration OTP v1\0").Concat(signingKey).ToArray());
    }
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record EncryptedValue(byte[] Ciphertext, byte[] Nonce, byte[] Tag);
    private sealed record Challenge(Guid Id, string OtpHash, int FailedAttempts, DateTime ExpiresAt, DateTime? VerifiedAt,
        DateTime? RegistrationTokenExpiresAt, DateTime? RegistrationTokenUsedAt, byte[]? RegistrationTokenCiphertext,
        byte[]? RegistrationTokenNonce, byte[]? RegistrationTokenTag);
}
