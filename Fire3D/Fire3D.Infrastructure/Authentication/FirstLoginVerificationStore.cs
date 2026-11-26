using System.Security.Cryptography;
using System.Text;
using Fire3D.Application.Authentication;
using Fire3D.Application.Authentication.Abstractions;
using Fire3D.Domain.Entities;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using static Fire3D.Application.Authentication.FirstLoginSteps;

namespace Fire3D.Infrastructure.Authentication;

/// <summary>
/// BE#235 first sign-in of an account created in VerifyEmail (see <see cref="FirstLoginSteps"/>). The challenge token and the code are
/// stored as hashes; the delivery job keeps the code encrypted until it is sent. Every state change runs under the account's auth lock.
/// </summary>
public sealed class FirstLoginVerificationStore(Fire3DDbContext db, IAuthStore accounts, ITokenService tokens, IIdentityProvider identities,
    IOptions<JwtOptions> jwt, TimeProvider clock) : IFirstLoginVerification, IFirstLoginDeliveryQueue
{
    private readonly byte[] key = DeriveKey(jwt.Value);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<AuthResult<FirstLoginChallenge>> StartAsync(User user, DateTime now, CancellationToken ct)
    {
        var row = await ReadAsync(user.Id, null, ct);
        var token = NewToken();
        // A live code is reused (no new email); a new password sign-in only gets a fresh challenge token.
        if (row is not null && row.OtpExpiresAt > now && row.FailedAttempts < MaxAttempts)
        {
            await ExecuteAsync("""
                UPDATE public.first_login_verifications SET challenge_token_hash=@hash,challenge_expires_at=@expires,updated_at=@now WHERE user_id=@id
                """, [Param("hash", Sha256(token)), Param("expires", now.Add(ChallengeLifetime)), Param("now", now), Param("id", user.Id)], ct);
            return AuthResult<FirstLoginChallenge>.Ok(new(token, now.Add(ChallengeLifetime), row.OtpExpiresAt, EmailHint(user.Email), ResendAfter(row, now)));
        }
        return await SendCodeAsync(user, row, token, now, ct);
    }

    public async Task<AuthResult<FirstLoginChallenge>> ResendAsync(FirstLoginResendRequest request, CancellationToken ct)
    {
        var now = Now;
        var resolved = await ResolveAsync(request?.ChallengeToken, now, ct);
        if (resolved.Error is not null) return new(default, resolved.Error);
        await using var tx = resolved.Transaction!;
        var result = await SendCodeAsync(resolved.User!, resolved.Row, request!.ChallengeToken, now, ct);
        if (result.IsSuccess) await tx.CommitAsync(ct);
        return result;
    }

    public async Task<AuthResult<LoginResponse>> VerifyCodeAsync(FirstLoginVerifyRequest request, CancellationToken ct)
    {
        if (request?.Code is not { Length: 6 } code || !code.All(char.IsAsciiDigit))
            return AuthResult<LoginResponse>.Fail("FIRST_LOGIN_CODE_INVALID", "Enter the 6-digit code from the email.", 400);
        var now = Now;
        var resolved = await ResolveAsync(request.ChallengeToken, now, ct);
        if (resolved.Error is not null) return new(default, resolved.Error);
        await using var tx = resolved.Transaction!;
        var row = resolved.Row!;
        if (row.FailedAttempts >= MaxAttempts) return Locked(row, now);
        if (row.OtpExpiresAt <= now)
            return new(default, new AuthError("FIRST_LOGIN_CODE_EXPIRED", "The code has expired. Request a new one.", 400,
                Extensions: new Dictionary<string, object?> { ["resendAfterSeconds"] = ResendAfter(row, now) }));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(HashCode(row.UserId, row.OtpVersion, code)), Encoding.ASCII.GetBytes(row.OtpHash)))
        {
            var attempts = row.FailedAttempts + 1;
            await ExecuteAsync("UPDATE public.first_login_verifications SET failed_attempts=@attempts,updated_at=@now WHERE user_id=@id",
                [Param("attempts", attempts), Param("now", now), Param("id", row.UserId)], ct);
            await tx.CommitAsync(ct);
            return attempts >= MaxAttempts ? Locked(row with { FailedAttempts = attempts }, now)
                : new(default, new AuthError("FIRST_LOGIN_CODE_INVALID", "The code is not correct.", 400,
                    Extensions: new Dictionary<string, object?> { ["attemptsRemaining"] = MaxAttempts - attempts }));
        }
        return await CompleteAsync(tx, resolved.User!, null, now, ct);
    }

    public async Task<AuthResult<LoginResponse>> VerifyGoogleAsync(FirstLoginGoogleRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.IdToken) || request.IdToken.Length > 16384)
            return AuthResult<LoginResponse>.Fail("INVALID_FIREBASE_TOKEN", "Google ID token is required.", 400);
        // The challenge must exist before Google is called; it is re-read under the lock afterwards.
        if (await ChallengeUserAsync(request.ChallengeToken, Now, ct) is null) return ChallengeInvalid<LoginResponse>();
        VerifiedIdentity identity;
        try { identity = await identities.VerifyGoogleTokenAsync(request.IdToken, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (GoogleIdentityException ex) when (ex.Failure == GoogleIdentityFailure.InvalidToken)
        { return AuthResult<LoginResponse>.Fail("INVALID_FIREBASE_TOKEN", "Invalid Firebase ID token.", 401); }
        catch (Exception)
        { return AuthResult<LoginResponse>.Fail("GOOGLE_PROVIDER_UNAVAILABLE", "Google verification is temporarily unavailable. Try again.", 503); }
        var now = Now;
        var resolved = await ResolveAsync(request.ChallengeToken, now, ct);
        if (resolved.Error is not null) return new(default, resolved.Error);
        await using var tx = resolved.Transaction!;
        var user = resolved.User!;
        if (string.IsNullOrWhiteSpace(identity.Uid) || identity.Uid.Length > 128
            || PasswordResetValidation.NormalizeEmail(identity.Email) is not { } googleEmail || googleEmail != PasswordResetValidation.NormalizeEmail(user.Email))
            return AuthResult<LoginResponse>.Fail("GOOGLE_EMAIL_MISMATCH", "Sign in with the Google account of the email this account was created with.", 403);
        string? link = null;
        if (user.FirebaseUid is null)
        {
            var other = await accounts.FindUserByFirebaseUidAsync(identity.Uid, ct);
            if (other is not null && other.Id != user.Id)
                return AuthResult<LoginResponse>.Fail("GOOGLE_ACCOUNT_IN_USE", "This Google account is linked to another account.", 409);
            link = identity.Uid;
        }
        else if (user.FirebaseUid != identity.Uid)
            return AuthResult<LoginResponse>.Fail("GOOGLE_EMAIL_MISMATCH", "This account is linked to another Google account.", 403);
        return await CompleteAsync(tx, user, link, now, ct);
    }

    public async Task<FirstLoginEmailJob?> ClaimAsync(CancellationToken ct)
    {
        var now = Now;
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            // A job is sent only while its code is the account's current, unexpired code.
            await NonQueryAsync("""
                UPDATE public.first_login_email_jobs j SET status='Dead',lease_token=NULL,lease_until=NULL
                 WHERE j.status IN('Pending','Leased') AND ((j.status='Leased' AND j.lease_until<=@now AND j.attempts>=5)
                    OR NOT EXISTS(SELECT 1 FROM public.first_login_verifications v WHERE v.user_id=j.user_id AND v.otp_version=j.otp_version AND v.otp_expires_at>@now))
                """, [Param("now", now)], ct);
            await using var command = Command("""
                WITH candidate AS (
                  SELECT j.id FROM public.first_login_email_jobs j
                  WHERE j.attempts<5 AND ((j.status='Pending' AND j.available_at<=@now) OR (j.status='Leased' AND j.lease_until<=@now))
                  ORDER BY j.created_at FOR UPDATE SKIP LOCKED LIMIT 1)
                UPDATE public.first_login_email_jobs j SET status='Leased',lease_token=@lease,lease_until=@until,attempts=attempts+1
                  FROM candidate x WHERE j.id=x.id
                RETURNING j.id,j.email,j.lease_token,j.attempts,j.otp_ciphertext,j.otp_nonce,j.otp_tag
                """);
            command.Parameters.AddRange(new NpgsqlParameter[] {Param("now", now), Param("lease", Guid.NewGuid()), Param("until", now.AddMinutes(2))});
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new(reader.GetGuid(0), reader.GetString(1), Decrypt((byte[])reader[4], (byte[])reader[5], (byte[])reader[6]), reader.GetGuid(2), reader.GetInt32(3));
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    public Task CompleteAsync(FirstLoginEmailJob job, CancellationToken ct) => ExecuteAsync("""
        UPDATE public.first_login_email_jobs SET status='Succeeded',lease_token=NULL,lease_until=NULL WHERE id=@id AND status='Leased' AND lease_token=@lease
        """, [Param("id", job.Id), Param("lease", job.LeaseToken)], ct);

    public Task FailAsync(FirstLoginEmailJob job, bool permanent, CancellationToken ct) => ExecuteAsync("""
        UPDATE public.first_login_email_jobs SET status=CASE WHEN @permanent OR attempts>=5 THEN 'Dead' ELSE 'Pending' END,
          available_at=@available,lease_token=NULL,lease_until=NULL WHERE id=@id AND status='Leased' AND lease_token=@lease
        """, [Param("permanent", permanent), Param("available", Now.AddSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Max(0, job.Attempt - 1))))),
            Param("id", job.Id), Param("lease", job.LeaseToken)], ct);

    // New code, sent at most once a minute and 5 times an hour per account; the challenge token stays the caller's.
    private async Task<AuthResult<FirstLoginChallenge>> SendCodeAsync(User user, Row? row, string token, DateTime now, CancellationToken ct)
    {
        if (row is not null && row.LastSentAt.Add(ResendCooldown) > now)
            return RateLimited<FirstLoginChallenge>(row.LastSentAt.Add(ResendCooldown) - now);
        var windowOpen = row is not null && row.SendWindowStartedAt.AddHours(1) > now;
        if (windowOpen && row!.SendsInWindow >= SendsPerHour) return RateLimited<FirstLoginChallenge>(row.SendWindowStartedAt.AddHours(1) - now);
        var version = (row?.OtpVersion ?? 0) + 1;
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var encrypted = Encrypt(code);
        await ExecuteAsync("""
            INSERT INTO public.first_login_verifications(user_id,challenge_token_hash,challenge_expires_at,otp_hash,otp_version,otp_expires_at,
              failed_attempts,last_sent_at,send_window_started_at,sends_in_window,created_at,updated_at)
            VALUES(@id,@hash,@challengeExpires,@otp,@version,@otpExpires,0,@now,@window,@sends,@now,@now)
            ON CONFLICT(user_id) DO UPDATE SET challenge_token_hash=EXCLUDED.challenge_token_hash,challenge_expires_at=EXCLUDED.challenge_expires_at,
              otp_hash=EXCLUDED.otp_hash,otp_version=EXCLUDED.otp_version,otp_expires_at=EXCLUDED.otp_expires_at,failed_attempts=0,
              last_sent_at=EXCLUDED.last_sent_at,send_window_started_at=EXCLUDED.send_window_started_at,sends_in_window=EXCLUDED.sends_in_window,updated_at=EXCLUDED.updated_at;
            UPDATE public.first_login_email_jobs SET status='Dead',lease_token=NULL,lease_until=NULL WHERE user_id=@id AND status IN('Pending','Leased');
            INSERT INTO public.first_login_email_jobs(id,user_id,otp_version,email,otp_ciphertext,otp_nonce,otp_tag,status,attempts,available_at,created_at)
            VALUES(@job,@id,@version,@email,@cipher,@nonce,@tag,'Pending',0,@now,@now);
            """, [Param("id", user.Id), Param("hash", Sha256(token)), Param("challengeExpires", now.Add(ChallengeLifetime)),
                Param("otp", HashCode(user.Id, version, code)), Param("version", version), Param("otpExpires", now.Add(CodeLifetime)), Param("now", now),
                Param("window", windowOpen ? row!.SendWindowStartedAt : now), Param("sends", windowOpen ? row!.SendsInWindow + 1 : 1),
                Param("job", Guid.NewGuid()), Param("email", user.Email), Param("cipher", encrypted.Ciphertext), Param("nonce", encrypted.Nonce),
                Param("tag", encrypted.Tag)], ct);
        return AuthResult<FirstLoginChallenge>.Ok(new(token, now.Add(ChallengeLifetime), now.Add(CodeLifetime), EmailHint(user.Email), (int)ResendCooldown.TotalSeconds));
    }

    // Verified: the email is proved, the password must change next. Opens the restricted session.
    private async Task<AuthResult<LoginResponse>> CompleteAsync(IAuthTransaction tx, User user, string? linkUid, DateTime now, CancellationToken ct)
    {
        await db.Users.Where(x => x.Id == user.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.EmailVerifiedAt, now).SetProperty(x => x.FirstLoginStep, ChangePassword)
            .SetProperty(x => x.LastLoginAt, now).SetProperty(x => x.UpdatedAt, now)
            .SetProperty(x => x.FirebaseUid, x => linkUid ?? x.FirebaseUid), ct);
        await ExecuteAsync("""
            DELETE FROM public.first_login_verifications WHERE user_id=@id;
            UPDATE public.first_login_email_jobs SET status='Dead',lease_token=NULL,lease_until=NULL WHERE user_id=@id AND status IN('Pending','Leased');
            """, [Param("id", user.Id)], ct);
        user.EmailVerifiedAt = now; user.FirstLoginStep = ChangePassword; user.LastLoginAt = now; user.UpdatedAt = now;
        if (linkUid is not null) user.FirebaseUid = linkUid;
        var family = Guid.NewGuid();
        var refresh = tokens.CreateRefreshToken();
        await accounts.AddRefreshTokenAsync(new RefreshToken
        {
            Id = Guid.NewGuid(), UserId = user.Id, FamilyId = family, TokenHash = tokens.HashRefreshToken(refresh), CreatedAt = now,
            ExpiresAt = now.Add(tokens.RefreshTokenLifetime)
        }, ct);
        await accounts.WriteAuditAsync(user, "Update", user.Id, now, ct);
        await accounts.WriteAuditAsync(user, "Login", user.Id, now, ct);
        var access = tokens.CreateAccessToken(user, family, now);
        await tx.CommitAsync(ct);
        return AuthResult<LoginResponse>.Ok(new(access.Value, refresh, GoogleAuthRules.ToAccount(user)));
    }

    // The challenge, locked: an active account still in VerifyEmail whose current challenge token matches and has not expired.
    private async Task<Resolved> ResolveAsync(string? token, DateTime now, CancellationToken ct)
    {
        var userId = await ChallengeUserAsync(token, now, ct);
        if (userId is null) return new(null, null, null, ChallengeInvalid<bool>().Error);
        var tx = await accounts.BeginUserTransactionAsync(userId.Value, ct);
        var row = await ReadAsync(userId.Value, Sha256(token!), ct);
        var user = await accounts.FindUserAsync(userId.Value, ct);
        if (row is null || row.ChallengeExpiresAt <= now || user is not { IsActive: true, DeletedAt: null, FirstLoginStep: VerifyEmail }
            || (user.OrganizationId is Guid organization && !await accounts.OrganizationIsActiveAsync(organization, ct)))
        {
            await tx.DisposeAsync();
            return new(null, null, null, ChallengeInvalid<bool>().Error);
        }
        return new(tx, row, user, null);
    }

    private async Task<Guid?> ChallengeUserAsync(string? token, DateTime now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return null;
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = Command("SELECT user_id FROM public.first_login_verifications WHERE challenge_token_hash=@hash AND challenge_expires_at>@now");
            command.Parameters.AddRange(new NpgsqlParameter[] {Param("hash", Sha256(token)), Param("now", now)});
            return await command.ExecuteScalarAsync(ct) as Guid?;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private async Task<Row?> ReadAsync(Guid userId, string? tokenHash, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = Command("""
                SELECT user_id,challenge_token_hash,challenge_expires_at,otp_hash,otp_version,otp_expires_at,failed_attempts,last_sent_at,send_window_started_at,sends_in_window
                FROM public.first_login_verifications WHERE user_id=@id AND (@hash::text IS NULL OR challenge_token_hash=@hash) FOR UPDATE
                """);
            command.Parameters.AddRange(new NpgsqlParameter[] {Param("id", userId), new NpgsqlParameter("hash", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)tokenHash ?? DBNull.Value }});
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new(reader.GetGuid(0), reader.GetString(1), reader.GetDateTime(2), reader.GetString(3), reader.GetInt32(4), reader.GetDateTime(5),
                reader.GetInt32(6), reader.GetDateTime(7), reader.GetDateTime(8), reader.GetInt32(9));
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static int ResendAfter(Row row, DateTime now) => Math.Max(0, (int)Math.Ceiling((row.LastSentAt.Add(ResendCooldown) - now).TotalSeconds));
    private static AuthResult<LoginResponse> Locked(Row row, DateTime now) => new(default, new AuthError("FIRST_LOGIN_CODE_LOCKED",
        "Too many wrong codes. Request a new code.", 400, Extensions: new Dictionary<string, object?> { ["resendAfterSeconds"] = ResendAfter(row, now) }));
    private static AuthResult<T> ChallengeInvalid<T>() =>
        AuthResult<T>.Fail("FIRST_LOGIN_CHALLENGE_INVALID", "This sign-in step has expired. Sign in with the email and password again.", 401);
    private static AuthResult<T> RateLimited<T>(TimeSpan wait) =>
        AuthResult<T>.Fail("OTP_RATE_LIMITED", "A code was sent recently. Try again after the indicated delay.", 429,
            retryAfterSeconds: Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)));

    private async Task<int> ExecuteAsync(string sql, IEnumerable<NpgsqlParameter> parameters, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try { return await NonQueryAsync(sql, parameters, ct); }
        finally { await db.Database.CloseConnectionAsync(); }
    }
    private async Task<int> NonQueryAsync(string sql, IEnumerable<NpgsqlParameter> parameters, CancellationToken ct)
    {
        await using var command = Command(sql);
        command.Parameters.AddRange(parameters.ToArray());
        return await command.ExecuteNonQueryAsync(ct);
    }
    private NpgsqlCommand Command(string sql) => new(sql, (NpgsqlConnection)db.Database.GetDbConnection())
    {
        Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction
    };
    private static NpgsqlParameter Param(string name, object? value) => new(name, value ?? DBNull.Value);

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private string HashCode(Guid userId, int version, string code) => Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{userId}:{version}:{code}")));
    private (byte[] Ciphertext, byte[] Nonce, byte[] Tag) Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var input = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[input.Length];
        var tag = new byte[16];
        using var cipher = new AesGcm(key, 16);
        cipher.Encrypt(nonce, input, ciphertext, tag);
        return (ciphertext, nonce, tag);
    }
    private string Decrypt(byte[] ciphertext, byte[] nonce, byte[] tag)
    {
        var plaintext = new byte[ciphertext.Length];
        using var cipher = new AesGcm(key, 16);
        cipher.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
    private static byte[] DeriveKey(JwtOptions options) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("FET3D first login code v1\0").Concat(Convert.FromBase64String(options.SigningKey)).ToArray());

    private sealed record Row(Guid UserId, string ChallengeTokenHash, DateTime ChallengeExpiresAt, string OtpHash, int OtpVersion, DateTime OtpExpiresAt,
        int FailedAttempts, DateTime LastSentAt, DateTime SendWindowStartedAt, int SendsInWindow);
    private sealed record Resolved(IAuthTransaction? Transaction, Row? Row, User? User, AuthError? Error);
}
