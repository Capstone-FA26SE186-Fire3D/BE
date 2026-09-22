using System.Security.Cryptography;
using System.Text;
using Fire3D.Application.Authentication;
using Fire3D.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fire3D.Infrastructure.Authentication;

public sealed class LocalPasswordReset(Fire3DDbContext db, IAuthStore accounts,
    IPasswordService passwords, IOptions<AuthEmailOptions> options) : ILocalPasswordReset
{
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public async Task<string?> CreateLinkAsync(string email, CancellationToken ct)
    {
        var user = await accounts.FindUserByEmailAsync(email, ct);
        if (user is null || !user.IsActive || user.DeletedAt.HasValue) return null;
        var url = options.Value.FrontendUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Configure AuthEmail:FrontendUrl.");
        var raw = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var hash = Hash(raw);
        await using var tx = await accounts.BeginUserTransactionAsync(user.Id, ct);
        user = await accounts.FindUserAsync(user.Id, ct);
        if (user is null || !user.IsActive || user.DeletedAt.HasValue) return null;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public.local_password_reset_tokens(id,user_id,token_hash,expires_at)
            VALUES ({Guid.NewGuid()},{user.Id},{hash},now()+interval '30 minutes')
            """,ct);
        await tx.CommitAsync(ct);
        return url.TrimEnd('/') + "/reset-password?token=" + raw;
    }

    public async Task<bool> ResetAsync(string token, string newPassword, CancellationToken ct)
    {
        var hash = Hash(token);
        var userId = await db.Database.SqlQuery<Guid>($"""
            SELECT user_id AS "Value" FROM public.local_password_reset_tokens
             WHERE token_hash={hash} AND used_at IS NULL AND expires_at>clock_timestamp()
            """).SingleOrDefaultAsync(ct);
        if (userId == Guid.Empty) return false;
        await using var tx = await accounts.BeginUserTransactionAsync(userId,ct);
        var user = await accounts.FindUserAsync(userId,ct);
        if (user is null || !user.IsActive || user.DeletedAt.HasValue) return false;
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE public.local_password_reset_tokens SET used_at=clock_timestamp()
             WHERE user_id={userId} AND token_hash={hash} AND used_at IS NULL AND expires_at>clock_timestamp()
            """,ct);
        if (changed != 1) return false;
        var passwordHash = passwords.Hash(user,newPassword);
        await db.Users.Where(x=>x.Id==userId).ExecuteUpdateAsync(s=>s
            .SetProperty(x=>x.PasswordHash,passwordHash).SetProperty(x=>x.UpdatedAt,DateTime.UtcNow),ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE public.local_password_reset_tokens SET used_at=clock_timestamp()
             WHERE user_id={userId} AND used_at IS NULL
            """,ct);
        await accounts.RevokeAllUserSessionsAsync(userId,DateTime.UtcNow,ct);
        await accounts.WriteAuditAsync(user,"Update",userId,DateTime.UtcNow,ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
