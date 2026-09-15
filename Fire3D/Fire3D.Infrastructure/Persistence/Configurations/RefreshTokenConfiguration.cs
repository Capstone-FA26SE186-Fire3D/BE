using Fire3D.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Fire3D.Infrastructure.Persistence.Configurations;

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> entity)
    {
        entity.ToTable("auth_refresh_tokens", "public");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(x => x.UserId).HasColumnName("user_id");
        entity.Property(x => x.FamilyId).HasColumnName("family_id");
        entity.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsRequired();
        entity.Property(x => x.CreatedAt).HasColumnName("created_at");
        entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        entity.Property(x => x.ConsumedAt).HasColumnName("consumed_at");
        entity.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        entity.HasIndex(x => x.TokenHash).IsUnique();
        entity.HasIndex(x => new { x.UserId, x.FamilyId });
        entity.HasIndex(x => x.ExpiresAt);
        entity.HasIndex(x => x.FamilyId).IsUnique().HasFilter("consumed_at IS NULL AND revoked_at IS NULL");
        entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
