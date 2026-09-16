using System;

namespace Fire3D.Domain.Entities;

public sealed class PasswordResetToken
{
    public Guid Id { get; set; }       // Token gửi trong link email (UUID)
    public Guid UserId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;
}
